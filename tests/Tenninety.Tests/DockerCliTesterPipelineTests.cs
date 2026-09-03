using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// End-to-end regressions over the REAL <see cref="DockerCli"/> adapter and the REAL
/// <see cref="DockerCliSandboxSession"/> with a scripted fake transport — no bypass and no
/// directly-constructed result for the Tester:
///
///  - Repair A: the Tester's complete bounded capture (the transport's 1 MiB combined cap is
///    the ONLY bound) survives the adapter boundary unchanged. There is no intermediate
///    presentation tail, so zero-test evidence near the beginning of large output still
///    reaches <see cref="TestOutputClassifier"/>; long legitimate output below the cap stays
///    acceptable; overflow stays fail-closed; and the final user-facing output remains
///    bounded after classification and sanitization.
///  - Repair B: a synthetic negative exit with NO operational flag (transport startup/I/O
///    failure) surfaces as a typed Tester infrastructure failure at the Tester boundary —
///    never an ordinary candidate failure — while flagged operational outcomes keep their
///    documented classification and cleanup/termination handling stays intact.
/// </summary>
public class DockerCliTesterPipelineTests
{
    private static readonly string ContainerId = new('1', 64);
    private static readonly string CandidateSha = new string('c', 40);

    private static DockerCliResult Ok(string stdout = "", string stderr = "") =>
        new(0, stdout, stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult SyntheticFailure() =>
        new(-1, "", "docker process failed to start: IOException", TimedOut: false,
            Cancelled: false, OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult TimedOut() =>
        new(-1, "", "", TimedOut: true, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Cancelled() =>
        new(-1, "", "", TimedOut: false, Cancelled: true,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Truncated() =>
        new(-1, "", "", TimedOut: false, Cancelled: false,
            OutputTruncated: true, Duration: TimeSpan.FromMilliseconds(1));

    private static string InspectJson(bool running, bool oom = false) =>
        "[{\"Id\":\"" + ContainerId + "\",\"Image\":\"sha256:" + new string('a', 64) +
        "\",\"State\":{\"Running\":" + (running ? "true" : "false") +
        ",\"Paused\":false,\"OOMKilled\":" + (oom ? "true" : "false") +
        ",\"ExitCode\":0},\"Config\":{},\"HostConfig\":{}}]";

    private static TesterRunContext MakeContext() => new()
    {
        Candidate = new CandidateRevision("main", CandidateSha, new string('d', 40)),
        WorkPackageId = "WP-001",
        Attempt = 3,
    };

    /// <summary>The REAL production stack under test: transport → DockerCli adapter →
    /// DockerCliSandboxSession → ShellTesterAgent. Nothing is bypassed.</summary>
    private static (ShellTesterAgent Agent, DockerCliSandboxSession Session) MakeStack(
        SessionScriptedTransport transport)
    {
        var cli = new DockerCli(transport);
        var session = new DockerCliSandboxSession(cli, ContainerId, SandboxRole.Tester,
            TimeSpan.FromMinutes(10));
        var agent = new ShellTesterAgent("dotnet build", "dotnet test {wp}",
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
        return (agent, session);
    }

    // ---- Repair A: complete bounded output through the adapter ---------------------

    [Fact]
    public async Task Zero_test_evidence_in_stdout_survives_the_adapter_with_more_than_32k_following_characters()
    {
        // The zero-test message sits at the very START of 70,000+ characters of output:
        // under 1 MiB (the transport cap is not exceeded) but far beyond the removed
        // 32 KiB presentation tail that used to destroy the evidence.
        var testStdout = "No tests found\n" + new string('a', 70_000);
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));
        transport.Enqueue(_ => Ok(testStdout));
        var (agent, session) = MakeStack(transport);

        var result = await agent.RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Contains("zero tests were executed", result.OutputTail);
        // The exact build/test command sequence reached the transport in order.
        AssertExecSequence(transport);
        // The final user-facing output remains bounded even though the capture was complete.
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars,
            $"the final report tail exceeded the bound: {result.OutputTail.Length}");
        Assert.Equal(CandidateSha, result.CandidateSha);
        AssertExecCount(transport, 2);
    }

    [Fact]
    public async Task Zero_test_evidence_in_stderr_survives_the_adapter_too()
    {
        var testStderr = "No tests were executed\n" + new string('b', 70_000);
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));
        transport.Enqueue(_ => Ok("", stderr: testStderr));
        var (agent, session) = MakeStack(transport);

        var result = await agent.RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Contains("zero tests were executed", result.OutputTail);
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars);
        AssertExecCount(transport, 2);
    }

    [Fact]
    public async Task Long_legitimate_successful_output_below_the_cap_still_passes()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));
        transport.Enqueue(_ => Ok(new string('x', 100_000) + "\nPassed!  - Failed: 0, Passed: 7"));
        var (agent, session) = MakeStack(transport);

        var result = await agent.RunAsync(session, MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        AssertExecCount(transport, 2);
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars);
    }

    [Fact]
    public async Task Actual_capture_overflow_remains_fail_closed_through_the_real_session()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));                  // build
        transport.Enqueue(_ => Truncated());                     // test: capture overflow
        transport.Enqueue(_ => Ok());                            // kill (session terminates)
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // termination proof
        var (agent, session) = MakeStack(transport);

        var result = await agent.RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Contains("truncated", result.OutputTail, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars);
        // The session proved termination and never returned to Running.
        Assert.Equal(SandboxSessionState.Failed, session.Info.State);
    }

    [Fact]
    public async Task Timeout_terminates_the_whole_container_and_keeps_the_failed_verdict_bounded()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));
        transport.Enqueue(_ => TimedOut());
        transport.Enqueue(_ => Ok());                            // kill
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // proof
        var (agent, session) = MakeStack(transport);

        var result = await agent.RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Contains("timed out", result.OutputTail);
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars);
        Assert.Equal(SandboxSessionState.Failed, session.Info.State);
        Assert.Equal(["kill", ContainerId], transport.Invocations[2].Arguments);
        Assert.Equal(["inspect", ContainerId], transport.Invocations[3].Arguments);
    }

    [Fact]
    public async Task Caller_cancellation_through_the_real_session_propagates()
    {
        var transport = new SessionScriptedTransport();
        using var cts = new CancellationTokenSource();
        // The caller token fires while the BUILD command runs; the agent then refuses to
        // submit the test command.
        transport.Enqueue(_ => { cts.Cancel(); return Ok("build ok"); });
        var (agent, session) = MakeStack(transport);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => agent.RunAsync(session, MakeContext(), cts.Token));
        // Only the build command was ever submitted; the test command was refused.
        var execCalls = transport.Invocations.Count(i => i.Arguments[0] == "exec");
        Assert.Equal(1, execCalls);
    }

    [Fact]
    public async Task An_infrastructure_cancellation_without_caller_cancellation_is_indeterminate_end_to_end()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));
        transport.Enqueue(_ => Cancelled());
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));
        var (agent, session) = MakeStack(transport);

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => agent.RunAsync(session, MakeContext()));
        Assert.Contains("indeterminate", ex.Message);
    }

    // ---- Repair B: synthetic negative exits are infrastructure failures ----------------

    [Fact]
    public async Task A_synthetic_startup_failure_during_the_build_is_an_infrastructure_failure()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => SyntheticFailure());              // exec: never started
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // OOM inspect after nonzero exit
        var (agent, session) = MakeStack(transport);

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => agent.RunAsync(session, MakeContext()));

        Assert.Contains("could not produce a definitive exit code", ex.Message);
        // Only the BUILD command was ever submitted: no test command follows the
        // indeterminate infrastructure failure.
        AssertExecCount(transport, 1);
        Assert.Equal(2, transport.Invocations.Count); // exec + OOM inspect
        Assert.Equal("exec", transport.Invocations[0].Arguments[0]);
    }

    [Fact]
    public async Task A_synthetic_failure_during_the_test_stage_after_a_successful_build_is_infrastructure()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));                  // build succeeds
        transport.Enqueue(_ => SyntheticFailure());              // test: never produced an exit
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // OOM inspect
        var (agent, session) = MakeStack(transport);

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => agent.RunAsync(session, MakeContext()));

        Assert.Contains("could not produce a definitive exit code", ex.Message);
        AssertExecCount(transport, 2);
        Assert.Equal(3, transport.Invocations.Count); // build exec, test exec, OOM inspect
    }

    [Fact]
    public async Task A_definitive_nonzero_exit_through_the_real_stack_stays_an_ordinary_failure()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok("build ok"));
        // A definitive nonzero exit (exit 1) is an ordinary candidate failure — never a
        // synthetic infrastructure failure.
        transport.Enqueue(_ => new DockerCliResult(1, "", "compile failed", TimedOut: false,
            Cancelled: false, OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)));
        transport.Enqueue(_ => Ok(InspectJson(running: false)));  // OOM inspect (not OOM)
        var (agent, session) = MakeStack(transport);

        var result = await agent.RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Contains("exited 1", result.OutputTail);
        var execCalls = transport.Invocations.Count(i => i.Arguments[0] == "exec");
        Assert.Equal(2, execCalls);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static void AssertExecSequence(SessionScriptedTransport transport)
    {
        Assert.True(transport.Invocations.Count >= 2);
        var build = transport.Invocations[0];
        var test = transport.Invocations[1];

        var expectedBuild = new List<string>
        {
            "exec",
            "--workdir", "/workspace",
            "--env", "TENNINETY_ATTEMPT=3",
            "--env", "TENNINETY_WP=WP-001",
            ContainerId,
            ShellTesterAgent.TesterShellExecutable,
            "--noprofile", "--norc", "-c", "dotnet build",
        };
        var expectedTest = new List<string>
        {
            "exec",
            "--workdir", "/workspace",
            "--env", "TENNINETY_ATTEMPT=3",
            "--env", "TENNINETY_WP=WP-001",
            ContainerId,
            ShellTesterAgent.TesterShellExecutable,
            "--noprofile", "--norc", "-c", "dotnet test WP-001",
        };
        Assert.Equal(expectedBuild, build.Arguments);
        Assert.Equal(expectedTest, test.Arguments);
    }

    private static void AssertExecCount(SessionScriptedTransport transport, int expected)
    {
        var execCalls = transport.Invocations.Count(i => i.Arguments[0] == "exec");
        Assert.Equal(expected, execCalls);
    }
}
