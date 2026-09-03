using System.Diagnostics;
using System.Text.RegularExpressions;
using Tenninety.Core.Models;
using Tenninety.Git;

namespace Tenninety.Execution.Testing;

/// <summary>
/// EXPLICIT compatibility implementation that runs the configured build/test commands with a
/// host shell in the authoritative checkout. It exists only for `sandbox.mode=unsafe-host`
/// operators who accept the loss of container isolation, and it is NEVER a fallback for an
/// unavailable Docker daemon, a failed preflight, an invalid image, a failed workspace
/// creation, a timeout, enabled restore, or a container startup failure — those paths fail
/// closed instead.
///
/// The authoritative repository dependency is injected through TRUSTED construction (the
/// factory), never carried through the Tester context. Every construction and every run
/// emits a prominent warning through the supplied logging path. No new host-shell execution
/// may be added anywhere else in the Tester path.
/// </summary>
public sealed partial class UnsafeHostTesterAgent : ITesterAgent
{
    public const string ModeWarning =
        "WARNING: sandbox.mode=unsafe-host — the mechanical test gate is running the " +
        "configured commands directly on the host in the authoritative checkout. Container " +
        "isolation is DISABLED for the Tester role by explicit configuration.";

    private readonly IGitService _authoritativeGit;
    private readonly string _testCommandTemplate;
    private readonly string _buildCommand;
    private readonly bool _failWhenNoProject;
    private readonly TimeSpan _attemptTimeout;
    private readonly Action<string>? _log;

    public UnsafeHostTesterAgent(
        IGitService authoritativeGit,
        string commandTemplate,
        string buildCommand,
        TimeSpan attemptTimeout,
        Action<string>? log = null,
        bool failWhenNoProject = true)
    {
        _authoritativeGit = authoritativeGit ?? throw new ArgumentNullException(nameof(authoritativeGit));
        _testCommandTemplate = commandTemplate ?? "";
        _buildCommand = buildCommand ?? "";
        _failWhenNoProject = failWhenNoProject;
        _attemptTimeout = attemptTimeout;
        _log = log;
        _log?.Invoke(ModeWarning);
    }

    public async Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
    {
        ctx.Validate();
        var candidateSha = ctx.Candidate.CommitSha;
        _log?.Invoke(ModeWarning);
        var repoPath = _authoritativeGit.RepoPath;

        var testProject = TestProjectDiscovery.FindTestProject(repoPath);
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
                    CandidateSha = candidateSha,
                }
                : new TestRunResult
                {
                    Passed = true,
                    ExitCode = 0,
                    Command = "(none – simulated pass)",
                    CandidateSha = candidateSha,
                };

        if (_failWhenNoProject && string.IsNullOrWhiteSpace(_testCommandTemplate))
            return new TestRunResult
            {
                Passed = false,
                ExitCode = -1,
                Command = "(discovery)",
                OutputTail = "live mode requires a non-empty test_command – failing closed.",
                CandidateSha = candidateSha,
            };

        // Optional build gate first: broken builds must fail before tests even start.
        if (!string.IsNullOrWhiteSpace(_buildCommand))
        {
            var build = await RunCommand(_buildCommand.Replace("{wp}", ctx.WorkPackageId),
                repoPath, ctx.WorkPackageId, ct);
            if (!build.Passed)
                return new TestRunResult
                {
                    Passed = false,
                    ExitCode = build.ExitCode,
                    Command = _buildCommand,
                    OutputTail = build.OutputTail,
                    CandidateSha = candidateSha,
                };
        }

        var test = await RunCommand(_testCommandTemplate.Replace("{wp}", ctx.WorkPackageId),
            repoPath, ctx.WorkPackageId, ct);
        var zeroTests = _failWhenNoProject && test.Passed &&
            ZeroTestsOutput().IsMatch(test.OutputTail);
        return new TestRunResult
        {
            Passed = test.Passed && !zeroTests,
            ExitCode = zeroTests ? -1 : test.ExitCode,
            Command = _testCommandTemplate,
            OutputTail = zeroTests
                ? test.OutputTail + "\nzero tests were executed - failing closed."
                : test.OutputTail,
            CandidateSha = candidateSha,
        };
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
        ChildProcessEnvironment.ApplyAllowlist(psi);        psi.Environment["TENNINETY_WP"] = wpId; // structured identity; never textually injected

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

    [GeneratedRegex(@"No test is available|No test matches|No tests? (?:were|was) (?:found|executed)|No tests? (?:found|executed)|Passed:\s*0\b|Total(?: tests)?:\s*0\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZeroTestsOutput();

    private static async Task KillAndWaitAsync(Process proc)
    {
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
    }
}
