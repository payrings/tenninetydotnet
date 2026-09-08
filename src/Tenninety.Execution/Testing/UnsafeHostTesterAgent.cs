using System.Diagnostics;
using System.Text;
using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;
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
/// Decision-input contract (mirrors the Docker tester): EVERY command's complete output is
/// captured under a hard SHARED aggregate bound of
/// <see cref="TestOutputClassifier.MaxCommandOutputBytes"/> (1 MiB) across BOTH streams —
/// stdout and stderr draw from one concurrency-safe byte budget
/// (<see cref="OutputBudget"/>), so a command can never claim two full caps. Zero-test and
/// success classification run against the COMPLETE captured output via the shared
/// <see cref="TestOutputClassifier"/> (never against a pre-shortened tail), and only while
/// the capture stayed within the aggregate limit; the presentation tail is shortened only
/// AFTER classification and sanitization. Output beyond the aggregate cap sets the typed
/// truncation flag, which can never pass — exceeding the cap fails closed instead of
/// classifying partial output. Zero-test detection applies to the TEST command only: a
/// successful build never fails because its output merely contains a zero-test-looking
/// phrase.
///
/// The authoritative repository dependency is injected through TRUSTED construction (the
/// factory), never carried through the Tester context. Every construction and every run
/// emits a prominent warning through the supplied logging path. No new host-shell execution
/// may be added anywhere else in the Tester path.
/// </summary>
public sealed class UnsafeHostTesterAgent : ITesterAgent
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
            if (!build.Succeeded)
            {
                // Zero-test phrases are NOT applied to the build stage: only a definitive or
                // operational build failure counts (same semantics as the Docker tester).
                var buildResult = TestOutputClassifier.ToTestRunResult(
                    TestOutputClassifier.Classify(build), _buildCommand, candidateSha,
                    zeroTestsFailClosed: false);
                return new TestRunResult
                {
                    Passed = false,
                    ExitCode = buildResult.ExitCode,
                    Command = _buildCommand,
                    OutputTail = TestOutputClassifier.FinalBound(
                        buildResult.OutputTail + "\nthe build failed; tests were never started."),
                    CandidateSha = candidateSha,
                };
            }
        }

        var test = await RunCommand(_testCommandTemplate.Replace("{wp}", ctx.WorkPackageId),
            repoPath, ctx.WorkPackageId, ct);
        return TestOutputClassifier.ToTestRunResult(
            TestOutputClassifier.Classify(test), _testCommandTemplate, candidateSha);
    }

    /// <summary>Bounded COMPLETE capture: the child's combined stdout/stderr is captured up
    /// to <see cref="TestOutputClassifier.MaxCommandOutputBytes"/> bytes SHARED across both
    /// streams (one aggregate budget); anything
    /// beyond the cap sets <c>OutputTruncated</c>, which can never pass (fail closed). The
    /// full decision input reaches <see cref="TestOutputClassifier"/>; shortening happens only
    /// inside the classifier AFTER classification and sanitization.</summary>
    private async Task<SandboxCommandResult> RunCommand(
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

        var startedAt = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_attemptTimeout);
        // ONE shared aggregate budget across stdout and stderr: the code and the report claim
        // a 1 MiB total decision-input limit, so both streams together draw from this single
        // concurrency-safe budget instead of each getting its own cap.
        var budget = new OutputBudget(TestOutputClassifier.MaxCommandOutputBytes);
        try
        {
            var stdoutTask = CaptureBoundedAsync(proc.StandardOutput.BaseStream, budget, timeoutCts.Token);
            var stderrTask = CaptureBoundedAsync(proc.StandardError.BaseStream, budget, timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var exitCode = proc.ExitCode;
            var truncated = stdout.Truncated || stderr.Truncated;
            return new SandboxCommandResult(
                ExitCode: exitCode,
                StdOutTail: stdout.Text,
                StdErrTail: stderr.Text,
                TimedOut: false,
                Cancelled: false,
                OomKilled: false,
                OutputTruncated: truncated,
                Duration: startedAt.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await KillAndWaitAsync(proc);
            return new SandboxCommandResult(
                ExitCode: -1,
                StdOutTail: "",
                StdErrTail: $"command timed out after {_attemptTimeout.TotalMinutes:0} minutes.",
                TimedOut: true,
                Cancelled: false,
                OomKilled: false,
                OutputTruncated: false,
                Duration: startedAt.Elapsed);
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

    /// <summary>Bounded COMPLETE capture: the child's combined stdout/stderr is captured up
    /// to the SHARED aggregate <paramref name="budget"/> (<see cref="OutputBudget"/>, 1 MiB
    /// across both streams); anything beyond the cap sets <c>OutputTruncated</c>, which can
    /// never pass (fail closed). The full decision input reaches
    /// <see cref="TestOutputClassifier"/>; shortening happens only inside the classifier AFTER
    /// classification and sanitization.</summary>
    private static async Task<(string Text, bool Truncated)> CaptureBoundedAsync(
        Stream stream, OutputBudget budget, CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var output = new MemoryStream();
        var truncated = false;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            // Reserve from the SHARED budget: whatever the budget does not grant is DISCARDED
            // (never buffered) and the typed truncation flag fails the command closed
            // downstream. A reservation is granted exactly once even when both streams race.
            var granted = budget.Reserve(read);
            if (granted < read) truncated = true;
            if (granted > 0) output.Write(buffer, 0, (int)granted);
        }
        return (Encoding.UTF8.GetString(output.ToArray()), truncated);
    }

    /// <summary>Concurrency-safe shared byte budget for the two output streams of one
    /// command. Reservations are granted atomically (interlocked compare-exchange), the sum
    /// of all grants never exceeds the limit, and any request beyond the remaining budget is
    /// only partially granted (or not at all), which the caller reports as truncation and
    /// which can never pass. Fail-closed by construction.</summary>
    internal sealed class OutputBudget(long limit)
    {
        private long _granted;
        private readonly long _limit = limit is > 0
            ? limit
            : throw new ArgumentOutOfRangeException(nameof(limit));

        /// <summary>Total bytes granted so far (both streams combined).</summary>
        public long Granted => Interlocked.Read(ref _granted);

        /// <summary>Atomically reserves up to <paramref name="requested"/> bytes from the
        /// shared budget and returns the granted count (0 &lt;= granted &lt;= requested).
        /// The sum of all grants across every caller never exceeds the limit.</summary>
        public long Reserve(long requested)
        {
            if (requested <= 0) return 0;
            while (true)
            {
                var granted = Interlocked.Read(ref _granted);
                var allow = Math.Min(requested, Math.Max(0, _limit - granted));
                if (Interlocked.CompareExchange(ref _granted, granted + allow, granted) == granted)
                    return allow;
            }
        }
    }

    private static async Task KillAndWaitAsync(Process proc)
    {
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
    }
}
