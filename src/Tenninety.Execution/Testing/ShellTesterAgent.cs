using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Execution.Testing;

/// <summary>
/// Container-only build/test execution for the offline Docker Tester. This class has NO
/// authoritative Git dependency, NO host-process execution method and NO filesystem access:
/// it receives an existing <see cref="ISandboxSession"/> (owned and disposed by trusted
/// orchestration) plus the logical <see cref="TesterRunContext"/> and command settings, and
/// submits every command through <see cref="ISandboxSession.RunAsync"/> as a STRUCTURED
/// argument vector. It never starts the configured shell on the host and never assembles a
/// `docker exec …` shell string.
///
/// Command shape (fixed):
///   Executable:        /bin/bash
///   Arguments:         --noprofile --norc -c &lt;one argument containing the configured command&gt;
///   WorkingDirectory:  /workspace
///   Environment:       TENNINETY_WP / TENNINETY_ATTEMPT (structured allowlist values only)
///
/// The configured build command runs first; tests run only when the build DEFINITIVELY
/// succeeded. `sandbox.roles.tester.timeout_seconds` is the OVERALL build-and-test budget:
/// elapsed time is tracked on a monotonic clock and each command receives no more than the
/// remaining positive budget and the permitted session timeout — never a fresh full budget.
/// </summary>
public sealed class ShellTesterAgent
{
    /// <summary>Exact fixed executable for tester commands.</summary>
    public const string TesterShellExecutable = "/bin/bash";

    private readonly string _buildCommand;
    private readonly string _testCommand;
    private readonly TimeSpan _overallBudget;
    private readonly TimeSpan _sessionTimeout;
    private readonly Func<TimeSpan>? _elapsedOverride;

    public ShellTesterAgent(
        string buildCommand,
        string testCommand,
        TimeSpan overallBudget,
        TimeSpan sessionTimeout,
        Func<TimeSpan>? elapsedOverride = null)
    {
        _buildCommand = buildCommand ?? "";
        _testCommand = testCommand ?? "";
        _overallBudget = overallBudget;
        _sessionTimeout = sessionTimeout;
        _elapsedOverride = elapsedOverride;
    }

    /// <summary>Runs build+test inside the given session. Result identity (candidate SHA)
    /// comes only from the trusted context — never from command output or the container.
    ///
    /// Cancellation/indeterminacy contract: the caller's token is checked before each
    /// command submission; a command result flagged `Cancelled` is propagated as caller
    /// cancellation when the caller's token actually fired, and as a typed indeterminate
    /// <see cref="TesterInfrastructureException"/> otherwise (a cancellation flag alone is
    /// never evidence of success and never falsely attributed to the user). A SYNTHETIC
    /// negative exit without any operational flag — the typed signature of a process that
    /// never produced a definitive exit (startup or transport I/O failure) — is likewise an
    /// indeterminate <see cref="TesterInfrastructureException"/>: it is never an ordinary
    /// candidate failure and never follows the Coder retry path. Zero-test detection applies
    /// to the TEST command only — a successful build never fails because its output merely
    /// contains a zero-test-looking phrase.</summary>
    public async Task<TestRunResult> RunAsync(
        ISandboxSession session, TesterRunContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ct.ThrowIfCancellationRequested();
        ctx.Validate();
        var candidateSha = ctx.Candidate.CommitSha;

        if (string.IsNullOrWhiteSpace(_buildCommand))
            throw TesterInfrastructureException.Controlled(
                "the live Docker tester requires a non-blank build_command; the tester " +
                "configuration is refused.");
        if (string.IsNullOrWhiteSpace(_testCommand))
            throw TesterInfrastructureException.Controlled(
                "the live Docker tester requires a non-blank test_command; the tester " +
                "configuration is refused.");

        var clock = _elapsedOverride is { } over ? over : NewMonotonicClock();

        // ---- build first ---------------------------------------------------------------
        var build = await RunCommand(session, _buildCommand, ctx, clock, ct);
        if (!build.Succeeded)
        {
            // Zero-test phrases are NOT applied to the build stage: a successful build may
            // legitimately print such text; only a definitive/operational failure counts.
            var failed = TestOutputClassifier.ToTestRunResult(
                build, _buildCommand, candidateSha, zeroTestsFailClosed: false);
            return new TestRunResult
            {
                Passed = false,
                ExitCode = failed.ExitCode,
                Command = failed.Command,
                OutputTail = TestOutputClassifier.FinalBound(
                    failed.OutputTail + "\nthe build failed; tests were never started."),
                CandidateSha = candidateSha,
            };
        }

        // ---- tests only after a definitive build success --------------------------------
        ct.ThrowIfCancellationRequested();
        var test = await RunCommand(session, _testCommand, ctx, clock, ct);
        return TestOutputClassifier.ToTestRunResult(test, _testCommand, candidateSha);
    }

    private async Task<TestOutputClassification> RunCommand(
        ISandboxSession session, string configuredCommand, TesterRunContext ctx,
        Func<TimeSpan> elapsed, CancellationToken ct)
    {
        var remaining = _overallBudget - elapsed();
        if (remaining <= TimeSpan.Zero)
            return new TestOutputClassification(
                ExitCode: -1,
                Succeeded: false,
                ZeroTestsDetected: false,
                OperationalReason: "the overall build-and-test budget was exhausted before this command could run.",
                ReportTail: "");

        var command = BuildCommand(configuredCommand, ctx, remaining, _sessionTimeout);
        var result = await session.RunAsync(command, ct);

        // A cancellation flag with an actually-cancelled caller token propagates as caller
        // cancellation; without the caller token it is an indeterminate infrastructure
        // failure — never a pass and never a user-cancellation claim.
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(
                "the tester command was interrupted by caller cancellation before it could " +
                "produce a definitive result.", ct);
        if (result.Cancelled)
            throw TesterInfrastructureException.Controlled(
                "the tester command was cancelled at the infrastructure layer without caller " +
                "cancellation; the result is indeterminate and the run is refused.");

        // A synthetic negative exit WITHOUT any operational flag is the typed signature of a
        // process that never produced a definitive exit (startup or transport I/O failure).
        // That is an infrastructure failure at the Tester boundary: the engine aborts the
        // run instead of retrying a candidate that never produced a definitive result.
        // Flagged outcomes (timeout, cancellation, OOM, truncation) keep their own
        // documented classification as ordinary operational failures.
        if (result.SyntheticInfrastructureFailure)
            throw TesterInfrastructureException.Controlled(
                "the tester command could not produce a definitive exit code (the docker exec " +
                "process could not be started or the transport failed during I/O); the result " +
                "is indeterminate and the run is refused.");

        return TestOutputClassifier.Classify(result);
    }

    /// <summary>Deterministic tester command construction: the configured command text is the
    /// single content of the `-c` argument (never concatenated, never re-parsed, never run on
    /// the host). {wp} template substitution happens only after the identifier was validated
    /// as a bounded ASCII identifier; TENNINETY_WP is always provided as a structured value.</summary>
    public static SandboxCommand BuildCommand(
        string configuredCommand, TesterRunContext ctx, TimeSpan remaining, TimeSpan sessionTimeout)
    {
        if (!TesterRunContext.IsValidWorkPackageIdentifier(ctx.WorkPackageId))
            throw new InvalidOperationException(
                "the work-package identifier must be validated before any tester command is built.");
        if (string.IsNullOrWhiteSpace(configuredCommand))
            throw new InvalidOperationException("the configured tester command must be non-blank.");
        if (remaining <= TimeSpan.Zero)
            throw new InvalidOperationException("the per-command budget must be positive.");

        // Each command never exceeds the remaining budget nor the permitted session timeout.
        var timeout = remaining < sessionTimeout ? remaining : sessionTimeout;
        var commandText = configuredCommand.Contains("{wp}", StringComparison.Ordinal)
            ? configuredCommand.Replace("{wp}", ctx.WorkPackageId, StringComparison.Ordinal)
            : configuredCommand;

        return new SandboxCommand
        {
            Executable = TesterShellExecutable,
            Arguments = ["--noprofile", "--norc", "-c", commandText],
            WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
            Timeout = timeout,
            MaxOutputBytes = TestOutputClassifier.MaxCommandOutputBytes,
            Environment = new Dictionary<string, string>
            {
                ["TENNINETY_WP"] = ctx.WorkPackageId,
                ["TENNINETY_ATTEMPT"] = ctx.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    private static Func<TimeSpan> NewMonotonicClock()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        return () => stopwatch.Elapsed;
    }
}
