using System.Diagnostics;
using System.Text;
using Tenninety.Core.Models;

namespace Tenninety.Execution;

/// <summary>
/// Shared machinery for coder agents that shell out to a terminal coding tool
/// (aider, OpenCode, Pi). Subclasses describe the invocation; this base owns the
/// process lifecycle, exit-code handling and the instruction template.
///
/// Contract with the engine: the tool edits the working tree and NEVER commits
/// (the engine owns every commit), and a non-zero exit throws so the failure is
/// classified by the engine's accounting.
///
/// Hardening (external review, Major 1 + minors):
/// - the child environment is an ALLOWLIST (PATH/HOME/locale/TLS roots) instead of
///   inheriting everything from the host, so host environment credentials are not inherited;
/// - every attempt is bounded by a timeout – a hung tool is killed (whole process
///   tree) and surfaces as a counted failure instead of freezing the site forever.
/// </summary>
public abstract class CliCoderAgentBase : ICoderAgent
{
    protected CliCoderAgentBase(TimeSpan attemptTimeout)
    {
        AttemptTimeout = attemptTimeout < TimeSpan.Zero ? TimeSpan.FromMinutes(10) : attemptTimeout;
    }

    protected TimeSpan AttemptTimeout { get; }

    /// <summary>Executable name resolved from the PATH.</summary>
    protected abstract string Executable { get; }

    /// <summary>Session artefact prefix to keep out of commits via .git/info/exclude, or null.</summary>
    protected virtual string? ArtefactPrefix => null;

    /// <summary>Command-line arguments for one implementation attempt.</summary>
    public abstract IReadOnlyList<string> BuildArguments(string instruction);

    /// <summary>Hook for agents that need extra environment variables beyond the allowlist.</summary>
    protected virtual void ConfigureEnvironment(System.Diagnostics.ProcessStartInfo psi) { }

    public async Task<CoderResult> ImplementAsync(WpContext ctx, CancellationToken ct = default)
    {
        if (ArtefactPrefix is not null) ExcludeArtefactsFromGit(ctx.RepoPath, ArtefactPrefix);

        var psi = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = ctx.RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in BuildArguments(BuildInstruction(ctx))) psi.ArgumentList.Add(arg);
        ChildProcessEnvironment.ApplyAllowlist(psi);
        ConfigureEnvironment(psi);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"failed to start '{Executable}' – is it installed and on the PATH?");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(AttemptTimeout);
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(timeoutCts.Token);
            var output = ((await stdoutTask) + " " + (await stderrTask)).Trim();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"{Executable} exited {proc.ExitCode}: {Truncate(Sanitise(output))}");
            return new CoderResult
            {
                ProducedChanges = true,
                Summary = $"{Executable}: implement {ctx.WorkPackage.Id}",
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await KillAndWaitAsync(proc);
            throw new InvalidOperationException(
                $"{Executable} attempt timed out after {AttemptTimeout.TotalMinutes:0} minutes and was killed.");
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(proc);
            throw;
        }
        finally
        {
            if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { /* racing exit */ } }
        }
    }

    /// <summary>The job card rendered as the agent's instruction. Identical for every backend.</summary>
    internal static string BuildInstruction(WpContext ctx)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Work package {ctx.WorkPackage.Id} ({ctx.WorkPackage.Layer}) – {ctx.WorkPackage.Title}");
        sb.AppendLine($"Goal: {Sanitise(ctx.WorkPackage.Goal)}");
        sb.AppendLine("Implement exactly these directives:");
        for (var i = 0; i < ctx.WorkPackage.Directives.Count; i++)
            sb.AppendLine($"{i + 1}. {Sanitise(ctx.WorkPackage.Directives[i])}");

        if (ctx.WorkPackage.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("The result must satisfy:");
            foreach (var criterion in ctx.WorkPackage.AcceptanceCriteria)
                sb.AppendLine($"- {Sanitise(criterion)}");
        }
        if (ctx.Global is not null)
        {
            var g = ctx.Global;
            if (g.CodingStandards.Count > 0)
            {
                sb.AppendLine("Coding standards to follow:");
                foreach (var standard in g.CodingStandards) sb.AppendLine($"- {Sanitise(standard)}");
            }
            if (g.Assumptions.Count > 0)
            {
                sb.AppendLine("Recorded project assumptions:");
                foreach (var assumption in g.Assumptions) sb.AppendLine($"- {Sanitise(assumption)}");
            }
            if (g.DirectoryStructure is { } dirs && dirs.Count > 0)
            {
                sb.AppendLine("Intended directory structure:");
                foreach (var (root, projects) in dirs)
                    sb.AppendLine($"- {root}: {string.Join(", ", projects)}");
            }
        }
        if (!string.IsNullOrWhiteSpace(ctx.WorkPackage.Notes))
            sb.AppendLine($"Notes: {Sanitise(ctx.WorkPackage.Notes)}");

        if (ctx.Advice.Count > 0)
        {
            sb.AppendLine("Repair advice from the architect – apply it:");
            foreach (var line in ctx.Advice.TakeLast(3)) sb.AppendLine($"- {Sanitise(line)}");
        }
        if (ctx.Feedback.Count > 0)
        {
            sb.AppendLine("Feedback from previous attempts – fix these points:");
            foreach (var line in ctx.Feedback.TakeLast(5)) sb.AppendLine($"- {Sanitise(line)}");
        }
        return Sanitise(sb.ToString());
    }

    /// <summary>Splits extra CLI flags on spaces while honouring double-quoted values.</summary>
    public static IReadOnlyList<string> SplitExtraArgs(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in raw)
        {
            switch (c)
            {
                case '"': inQuotes = !inQuotes; break;
                case ' ' when !inQuotes:
                    if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                    break;
                default: current.Append(c); break;
            }
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static string Sanitise(string s) => Core.Security.Sanitizer.SanitizeText(s ?? "");

    private static string Truncate(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "…";

    private static async Task KillAndWaitAsync(Process proc)
    {
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
    }

    private static void ExcludeArtefactsFromGit(string repoPath, string artefactPrefix)
    {
        var excludeFile = Path.Combine(
            DaemonLock.ResolveCommonGitDirectory(repoPath), "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(excludeFile)!);
        var lines = File.Exists(excludeFile) ? File.ReadAllLines(excludeFile).ToList() : new List<string>();
        if (lines.Any(l => l.Trim() == artefactPrefix)) return;
        lines.Add(artefactPrefix);
        File.WriteAllLines(excludeFile, lines);
    }
}
