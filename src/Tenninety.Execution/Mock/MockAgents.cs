using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text;
using System.Xml.Linq;
using Tenninety.Core;
using Tenninety.Core.Models;

namespace Tenninety.Execution.Mock;

/// <summary>
/// Offline coder: materializes the WP as a deterministic implementation note file and commits it.
/// Lets the full queue run end-to-end with no models (Phase 1 exit criterion).
/// </summary>
public sealed class MockCoderAgent : ICoderAgent
{
    public Task<CoderResult> ImplementAsync(WpContext ctx, CancellationToken ct = default)
    {
        var dir = Path.Combine(ctx.RepoPath, "app");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{ctx.WorkPackage.Id}.implementation.md");

        var sb = new StringBuilder();
        sb.AppendLine($"# {ctx.WorkPackage.Id} — {ctx.WorkPackage.Title}");
        sb.AppendLine($"Layer: {ctx.WorkPackage.Layer}");
        sb.AppendLine($"Goal: {ctx.WorkPackage.Goal}");
        sb.AppendLine();
        sb.AppendLine("## Directives");
        foreach (var d in ctx.WorkPackage.Directives) sb.AppendLine($"- {d}");
        sb.AppendLine();
        sb.AppendLine("## Acceptance Criteria");
        foreach (var a in ctx.WorkPackage.AcceptanceCriteria) sb.AppendLine($"- {a}");
        if (!string.IsNullOrWhiteSpace(ctx.WorkPackage.Notes))
        {
            sb.AppendLine();
            sb.AppendLine($"## Notes ({ctx.WorkPackage.Module})");
            sb.AppendLine(ctx.WorkPackage.Notes.Trim());
        }
        if (ctx.Advice.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Frontier Repair Advice Applied");
            foreach (var a in ctx.Advice) sb.AppendLine($"- {a}");
        }
        if (ctx.Feedback.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Feedback Incorporated");
            foreach (var f in ctx.Feedback.TakeLast(5)) sb.AppendLine($"- {f}");
        }
        File.WriteAllText(file, sb.ToString());

        var summary = $"attempt {ctx.Attempt}: materialized directives for {ctx.WorkPackage.Id}";
        return Task.FromResult(new CoderResult
        {
            ProducedChanges = true,
            Summary = summary,
            FilesTouched = new List<string> { Path.GetRelativePath(ctx.RepoPath, file) },
        });
    }
}

/// <summary>Deterministic reviewer: fails the first N attempts of a phase, always passes once frontier advice exists.</summary>
public sealed class MockReviewerAgent : IReviewerAgent
{
    private readonly int _failAttempts;
    private readonly bool _ignoresAdvice;

    public MockReviewerAgent(int failAttempts = 0, bool ignoresAdvice = false)
    {
        _failAttempts = failAttempts;
        _ignoresAdvice = ignoresAdvice;
    }

    public Task<ReviewResult> ReviewAsync(WpContext ctx, CancellationToken ct = default)
    {
        if (!_ignoresAdvice && (ctx.Advice.Count > 0 || ctx.Attempt > _failAttempts))
            return Task.FromResult(new ReviewResult { Passed = true, ReviewerModel = "mock-reviewer" });

        return Task.FromResult(new ReviewResult
        {
            Passed = false,
            ReviewerModel = "mock-reviewer",
            Reasons = new List<string>
            {
                $"Directive not yet demonstrably satisfied: '{ctx.WorkPackage.Directives[Math.Min(ctx.Attempt - 1, ctx.WorkPackage.Directives.Count - 1)]}'.",
                "Implementation note does not map every directive to a concrete change.",
            },
        });
    }
}

/// <summary>
/// Mechanical test gate. Live mode is FAIL-CLOSED (external review finding 7): when no test
/// project can be discovered, or the configured build/test commands are empty, the gate fails
/// with an explicit reason instead of silently passing. Discovery walks the whole workspace
/// (nested solutions are common); a configurable build command runs before the tests, both
/// bounded by a timeout that kills the whole process tree.
/// </summary>
public sealed partial class ShellTesterAgent : ITesterAgent
{
    private readonly string _testCommandTemplate;
    private readonly string _buildCommand;
    private readonly bool _failWhenNoProject;
    private readonly int _simulatedFailAttempts;
    private readonly TimeSpan _attemptTimeout;
    private readonly Action<string>? _log;

    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules", ".tenninety"];

    public ShellTesterAgent(
        string commandTemplate,
        int simulatedFailAttempts = 0,
        Action<string>? log = null,
        bool failWhenNoProject = false,
        string buildCommand = "",
        TimeSpan? attemptTimeout = null)
    {
        _testCommandTemplate = commandTemplate ?? "";
        _buildCommand = buildCommand ?? "";
        _failWhenNoProject = failWhenNoProject;
        _simulatedFailAttempts = simulatedFailAttempts;
        _attemptTimeout = attemptTimeout ?? TimeSpan.FromMinutes(20);
        _log = log;
    }

    public async Task<TestRunResult> RunTestsAsync(WpContext ctx, CancellationToken ct = default)
    {
        // Simulated failure window for exercising retry/escalation paths without a real suite.
        if (_simulatedFailAttempts > 0 && ctx.Attempt <= _simulatedFailAttempts && ctx.Advice.Count == 0)
            return new TestRunResult
            {
                Passed = false,
                ExitCode = 1,
                Command = "(simulated)",
                OutputTail = $"Simulated mechanical failure on attempt {ctx.Attempt}.",
            };

        var testProject = DiscoverTestProject(ctx.RepoPath);
        if (testProject is null)
            return _failWhenNoProject
                ? new TestRunResult
                {
                    Passed = false,
                    ExitCode = -1,
                    Command = "(discovery)",
                    OutputTail = "no test project found anywhere in the workspace " +
                                 "(a csproj referencing xunit/nunit/mstest or marked IsTestProject) – failing closed. " +
                                 "An application-only solution runs zero tests and cannot gate a promotion.",
                }
                : new TestRunResult { Passed = true, ExitCode = 0, Command = "(none – simulated pass)" };

        if (_failWhenNoProject && string.IsNullOrWhiteSpace(_testCommandTemplate))
            return new TestRunResult
            {
                Passed = false,
                ExitCode = -1,
                Command = "(discovery)",
                OutputTail = "live mode requires a non-empty test_command – failing closed.",
            };

        // Optional build gate first: broken builds must fail before tests even start.
        if (!string.IsNullOrWhiteSpace(_buildCommand))
        {
            var build = await RunCommand(_buildCommand.Replace("{wp}", ctx.WorkPackage.Id),
                ctx.RepoPath, ctx.WorkPackage.Id, ct);
            if (!build.Passed)
                return new TestRunResult
                {
                    Passed = false,
                    ExitCode = build.ExitCode,
                    Command = _buildCommand,
                    OutputTail = build.OutputTail,
                };
        }

        var test = await RunCommand(_testCommandTemplate.Replace("{wp}", ctx.WorkPackage.Id),
            ctx.RepoPath, ctx.WorkPackage.Id, ct);
        var zeroTests = _failWhenNoProject && test.Passed && ZeroTestsOutput().IsMatch(test.OutputTail);
        return new TestRunResult
        {
            Passed = test.Passed && !zeroTests,
            ExitCode = zeroTests ? -1 : test.ExitCode,
            Command = cmd2(_testCommandTemplate),
            OutputTail = zeroTests
                ? test.OutputTail + "\nzero tests were executed - failing closed."
                : test.OutputTail,
        };

        static string cmd2(string t) => t;
    }

    private async Task<(bool Passed, int ExitCode, string OutputTail)> RunCommand(
        string command, string workDir, string wpId, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "--noprofile", "--norc", "-c", command },
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ChildProcessEnvironment.ApplyAllowlist(psi);
        psi.Environment["TENNINETY_WP"] = wpId; // structured identity; never textually injected

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the shell for the test command.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_attemptTimeout);
        try
        {
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(timeoutCts.Token);
            var output = ((await stdoutTask) + Environment.NewLine + (await stderrTask)).Trim();
            const int maxTail = 4000;
            var tail = output.Length <= maxTail ? output : output[^maxTail..];
            return (proc.ExitCode == 0, proc.ExitCode, tail);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await KillAndWaitAsync(proc);
            return (false, -1, $"command timed out after {_attemptTimeout.TotalMinutes:0} minutes.");
        }
        catch (OperationCanceledException)
        {
            await KillAndWaitAsync(proc);
            throw;
        }
        finally
        {
            if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        }
    }

    /// <summary>Any buildable project/solution – used by the BUILD gate.</summary>
    internal static string? DiscoverProject(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 12,
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", options))
        {
            if (SkippedDirectories.Any(d => entry.Contains(
                    $"{Path.DirectorySeparatorChar}{d}{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            var name = Path.GetFileName(entry);
            if (name.EndsWith(".sln") || name.EndsWith(".slnx") || name.EndsWith(".csproj"))
                return entry;
        }
        return null;
    }

    /// <summary>Whole-workspace discovery of a TEST project: its csproj must reference a
    /// recognised test framework or be flagged IsTestProject. Application-only solutions
    /// deliberately do NOT satisfy the gate (external review Major 6).</summary>
    internal static string? DiscoverTestProject(string root)
    {
        foreach (var entry in Directory.EnumerateFiles(root, "*.csproj", EnumerationOptionsForDiscovery()))
        {
            if (Skips(entry)) continue;
            try
            {
                var project = XDocument.Load(entry);
                var isTestProject = project.Descendants()
                    .Any(e => e.Name.LocalName == "IsTestProject" &&
                              bool.TryParse(e.Value.Trim(), out var value) && value);
                var hasTestPackage = project.Descendants()
                    .Where(e => e.Name.LocalName == "PackageReference")
                    .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "")
                    .Any(IsTestPackage);
                if (isTestProject || hasTestPackage) return entry;
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException)
            {
                // Malformed/inaccessible project files do not prove that a runnable suite exists.
            }
        }
        return null;

        static bool Skips(string path) =>
            new[] { "/bin/", "/obj/", "/.git/", "/node_modules/" }
                .Any(skip => path.Contains(skip, StringComparison.OrdinalIgnoreCase));

        static bool IsTestPackage(string package) =>
            package.Equals("xunit", StringComparison.OrdinalIgnoreCase) ||
            package.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase) ||
            package.Equals("nunit", StringComparison.OrdinalIgnoreCase) ||
            package.StartsWith("nunit.", StringComparison.OrdinalIgnoreCase) ||
            package.StartsWith("mstest.", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"No test is available|No test matches|No tests? (?:were|was) (?:found|executed)|Passed:\s*0\b|Total(?: tests)?:\s*0\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZeroTestsOutput();

    private static EnumerationOptions EnumerationOptionsForDiscovery() => new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 12,
    };

    private static async Task KillAndWaitAsync(Process proc)
    {
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
    }
}
