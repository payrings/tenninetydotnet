using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Fakes for the container-only ShellTesterAgent: they record the exact requested commands and
/// return scripted results. No submitted command text is ever executed on the host.
/// </summary>
public sealed class RecordingSandboxSession : ISandboxSession
{
    public List<SandboxCommand> Commands { get; } = new();
    public List<string> Events { get; } = new();
    public string? SourcePath { get; set; }

    /// <summary>Invoked with the recorded command before the scripted result is returned.</summary>
    public Action<SandboxCommand>? OnRun { get; set; }

    /// <summary>Optional unified timeline sink for lifecycle events (stop/dispose).</summary>
    public Action<string>? EventSink { get; set; }
    public bool ThrowOnRun { get; set; }
    public bool ThrowOnDispose { get; set; }
    public bool ThrowOnStop { get; set; }

    /// <summary>When true (default, the historical behavior) a caller cancellation observed
    /// inside RunAsync throws OperationCanceledException. When false the scripted result is
    /// RETURNED instead — reproducing the real Docker session path that terminates the
    /// container and reports <c>Cancelled=true</c> without throwing.</summary>
    public bool ThrowOnCallerCancellation { get; set; } = true;
    public SandboxRole Role { get; set; } = SandboxRole.Tester;

    private readonly Queue<SandboxCommandResult> _scripted = new();

    public SandboxSessionInfo Info =>
        new(new string('f', 64), Role, SandboxSessionState.Running);

    /// <summary>Script the result for the next RunAsync call; when the queue is empty a
    /// clean successful result is returned.</summary>
    public RecordingSandboxSession Then(SandboxCommandResult result)
    {
        _scripted.Enqueue(result);
        return this;
    }

    public static SandboxCommandResult Ok(string stdout = "") => new(
        0, stdout, "", TimedOut: false, Cancelled: false,
        OomKilled: false, OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    public static SandboxCommandResult Fail(int exitCode = 1, string stdout = "", string stderr = "") => new(
        exitCode, stdout, stderr, TimedOut: false, Cancelled: false,
        OomKilled: false, OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken ct = default)
    {
        Commands.Add(command);
        OnRun?.Invoke(command);
        if (ct.IsCancellationRequested && ThrowOnCallerCancellation)
            throw new OperationCanceledException("simulated caller cancellation", ct);
        if (ThrowOnRun)
            throw new InvalidOperationException("simulated session command failure");
        return Task.FromResult(_scripted.Count > 0 ? _scripted.Dequeue() : Ok());
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        Events.Add("stop");
        EventSink?.Invoke("stop");
        if (ThrowOnStop)
            throw new InvalidOperationException("simulated: the container stop could not be proven");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Events.Add("dispose");
        EventSink?.Invoke("dispose");
        if (ThrowOnDispose)
            throw new InvalidOperationException("simulated: container removal could not be proven");
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Container-only execution behavior: exact argument vectors, /workspace, structured
/// environment, build-before-test, budget behavior, and candidate identity binding. The
/// submitted command text is never executed on the host.
/// </summary>
public class ShellTesterAgentTests
{
    private static readonly string CandidateSha = new string('c', 40);

    private static TesterRunContext MakeContext(string wpId = "WP-001", int attempt = 3) => new()
    {
        Candidate = new CandidateRevision("work/WP-001", CandidateSha, new string('d', 40)),
        WorkPackageId = wpId,
        Attempt = attempt,
    };

    private static ShellTesterAgent MakeAgent(
        string build = "dotnet build",
        string test = "dotnet test {wp}",
        TimeSpan? budget = null,
        TimeSpan? sessionTimeout = null,
        Func<TimeSpan>? elapsed = null) =>
        new(build, test, budget ?? TimeSpan.FromMinutes(10),
            sessionTimeout ?? TimeSpan.FromMinutes(10), elapsed);

    // ---- command construction -----------------------------------------------------------

    [Fact]
    public async Task Empty_build_or_test_commands_are_refused_as_infrastructure_without_touching_the_session()
    {
        var session = new RecordingSandboxSession();

        await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeAgent(build: "").RunAsync(session, MakeContext()));
        await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeAgent(test: "").RunAsync(session, MakeContext()));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task The_submitted_command_uses_the_exact_shell_vector_and_workspace()
    {
        var session = new RecordingSandboxSession();

        await MakeAgent(test: "dotnet test {wp}").RunAsync(session, MakeContext());

        Assert.Equal(2, session.Commands.Count);
        var test = session.Commands[1];
        Assert.Equal("/bin/bash", test.Executable);
        Assert.Equal(new[] { "--noprofile", "--norc", "-c", "dotnet test WP-001" },
            test.Arguments);
        Assert.Equal("/workspace", test.WorkingDirectory);
        Assert.Equal(TestOutputClassifier.MaxCommandOutputBytes, test.MaxOutputBytes);
    }

    [Fact]
    public async Task The_environment_is_structured_and_allowlisted_only()
    {
        var session = new RecordingSandboxSession();

        await MakeAgent().RunAsync(session, MakeContext(wpId: "WP-009", attempt: 4));

        var expected = new Dictionary<string, string>
        {
            ["TENNINETY_WP"] = "WP-009",
            ["TENNINETY_ATTEMPT"] = "4",
        };
        Assert.All(session.Commands, c =>
        {
            Assert.Equal(
                expected.OrderBy(kv => kv.Key),
                c.Environment.OrderBy(kv => kv.Key));
        });
    }

    [Fact]
    public void An_invalid_work_package_identifier_is_rejected_before_any_command_exists()
    {
        var ctx = MakeContext(wpId: "../../evil");
        Assert.Throws<InvalidOperationException>(() => ShellTesterAgent.BuildCommand(
            "dotnet test", ctx, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void The_per_command_timeout_never_exceeds_the_remaining_budget_or_session_timeout()
    {
        var ctx = MakeContext();
        var command = ShellTesterAgent.BuildCommand(
            "dotnet test", ctx, remaining: TimeSpan.FromSeconds(3),
            sessionTimeout: TimeSpan.FromMinutes(10));
        Assert.Equal(TimeSpan.FromSeconds(3), command.Timeout);

        var capped = ShellTesterAgent.BuildCommand(
            "dotnet test", ctx, remaining: TimeSpan.FromMinutes(10),
            sessionTimeout: TimeSpan.FromMinutes(2));
        Assert.Equal(TimeSpan.FromMinutes(2), capped.Timeout);
    }

    // ---- ordering and failure semantics ---------------------------------------------------

    [Fact]
    public async Task The_build_runs_before_the_tests()
    {
        var session = new RecordingSandboxSession();

        var result = await MakeAgent(
            build: "dotnet build", test: "dotnet test").RunAsync(session, MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(2, session.Commands.Count);
        Assert.Contains("dotnet build", session.Commands[0].Arguments[^1]);
        Assert.Contains("dotnet test", session.Commands[1].Arguments[^1]);
    }

    [Fact]
    public async Task A_build_failure_prevents_the_tests_from_ever_running()
    {
        var session = new RecordingSandboxSession();
        session.Then(RecordingSandboxSession.Fail(exitCode: 2, stdout: "compile error"));

        var result = await MakeAgent().RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Single(session.Commands); // test command never submitted
        Assert.Contains("build failed", result.OutputTail);
        Assert.Equal(2, result.ExitCode);
        Assert.Equal(CandidateSha, result.CandidateSha);
        Assert.Equal("dotnet build", result.Command); // the failure belongs to the BUILD stage
    }

    [Fact]
    public async Task A_successful_build_containing_a_zero_test_phrase_still_runs_the_tests()
    {
        var session = new RecordingSandboxSession();
        session.Then(RecordingSandboxSession.Ok("build ok; No tests found in this filter"))
               .Then(RecordingSandboxSession.Ok("Passed!  - Failed: 0, Passed: 5"));

        var result = await MakeAgent().RunAsync(session, MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(2, session.Commands.Count); // the zero-test phrase did not stop the tests
    }

    [Fact]
    public async Task Operational_failures_on_the_test_command_prevent_a_pass()
    {
        // exit code 0 but the test command timed out: never a pass. The build result is
        // scripted separately so the timeout is actually consumed by the TEST stage.
        var session = new RecordingSandboxSession();
        session.Then(RecordingSandboxSession.Ok("build ok"))
               .Then(new SandboxCommandResult(
                   0, "all green", "", TimedOut: true, Cancelled: false,
                   OomKilled: false, OutputTruncated: false, Duration: TimeSpan.FromSeconds(9)));

        var result = await MakeAgent().RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Equal(2, session.Commands.Count);
        Assert.Contains("timed out", result.OutputTail);
        Assert.Equal("dotnet test {wp}", result.Command); // the failure belongs to the TEST stage
    }

    [Fact]
    public async Task Zero_test_output_in_a_successful_test_command_fails_closed()
    {
        var session = new RecordingSandboxSession();
        session.Then(RecordingSandboxSession.Ok("build ok"))
               .Then(RecordingSandboxSession.Ok("No test is available in the selected project"));

        var result = await MakeAgent().RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Equal(2, session.Commands.Count);
        Assert.Contains("zero tests were executed", result.OutputTail);
        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("dotnet test {wp}", result.Command);
    }

    [Theory]
    [InlineData("No tests found")]
    [InlineData("No tests executed")]
    [InlineData("no tests found in the assembly")]
    public async Task The_new_zero_test_forms_fail_closed_on_the_test_stage(string phrase)
    {
        var session = new RecordingSandboxSession();
        session.Then(RecordingSandboxSession.Ok("build ok"))
               .Then(RecordingSandboxSession.Ok(phrase));

        var result = await MakeAgent().RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Equal(2, session.Commands.Count);
        Assert.Contains("zero tests were executed", result.OutputTail);
    }

    [Fact]
    public async Task A_zero_test_message_outside_the_report_tail_still_fails_the_gate()
    {
        var session = new RecordingSandboxSession();
        session.Then(RecordingSandboxSession.Ok("build ok"))
               .Then(RecordingSandboxSession.Ok("Total tests: 0\n" + new string('x', 9000)));

        var result = await MakeAgent().RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Equal(2, session.Commands.Count);
        Assert.Contains("zero tests were executed", result.OutputTail);
    }

    [Fact]
    public async Task An_infrastructure_cancellation_flag_without_caller_cancellation_is_indeterminate()
    {
        // The real Docker session terminates the container and RETURNS Cancelled=true
        // without throwing. Without a caller cancellation that is an indeterminate
        // infrastructure failure: never a pass, never attributed to the user.
        var session = new RecordingSandboxSession { ThrowOnCallerCancellation = false };
        session.Then(new SandboxCommandResult(
            0, "partial", "", TimedOut: false, Cancelled: true,
            OomKilled: false, OutputTruncated: false, Duration: TimeSpan.FromSeconds(3)));

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeAgent().RunAsync(session, MakeContext()));

        Assert.Contains("indeterminate", ex.Message);
        Assert.Single(session.Commands);
    }

    [Fact]
    public async Task Caller_cancellation_before_the_build_submits_nothing()
    {
        var session = new RecordingSandboxSession();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MakeAgent().RunAsync(session, MakeContext(), cts.Token));
        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_build_prevents_the_test_submission()
    {
        // The fake session cancels the caller token and RETURNS a (successful-looking)
        // result instead of throwing — the production path must still stop before tests.
        var session = new RecordingSandboxSession { ThrowOnCallerCancellation = false };
        using var cts = new CancellationTokenSource();
        session.OnRun = _ => cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MakeAgent().RunAsync(session, MakeContext(), cts.Token));
        Assert.Single(session.Commands); // the test command was never submitted
    }

    [Fact]
    public async Task Caller_cancellation_during_the_test_command_propagates()
    {
        var session = new RecordingSandboxSession { ThrowOnCallerCancellation = false };
        using var cts = new CancellationTokenSource();
        var seen = 0;
        session.OnRun = _ =>
        {
            if (++seen == 2) cts.Cancel(); // cancel while the TEST command runs
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MakeAgent().RunAsync(session, MakeContext(), cts.Token));
        Assert.Equal(2, session.Commands.Count);
    }

    // ---- presentation bounds ---------------------------------------------------------------

    [Theory]
    [InlineData("ordinary-failure")]
    [InlineData("zero-tests")]
    [InlineData("timeout")]
    [InlineData("build-failure")]
    public async Task The_final_report_tail_stays_bounded_for_every_failure_shape(string scenario)
    {
        var noise = new string('x', 9000);
        var session = new RecordingSandboxSession();
        TestRunResult result;
        switch (scenario)
        {
            case "ordinary-failure":
                session.Then(RecordingSandboxSession.Ok("build ok"))
                       .Then(RecordingSandboxSession.Fail(3, noise));
                result = await MakeAgent().RunAsync(session, MakeContext());
                break;
            case "zero-tests":
                session.Then(RecordingSandboxSession.Ok("build ok"))
                       .Then(RecordingSandboxSession.Ok("No tests found\n" + noise));
                result = await MakeAgent().RunAsync(session, MakeContext());
                break;
            case "timeout":
                session.Then(RecordingSandboxSession.Ok("build ok"))
                       .Then(new SandboxCommandResult(0, noise, "", TimedOut: true, Cancelled: false,
                           OomKilled: false, OutputTruncated: false,
                           Duration: TimeSpan.FromSeconds(5)));
                result = await MakeAgent().RunAsync(session, MakeContext());
                break;
            default:
                session.Then(RecordingSandboxSession.Fail(2, noise));
                result = await MakeAgent().RunAsync(session, MakeContext());
                break;
        }

        Assert.False(result.Passed);
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars,
            $"the final report tail exceeded the bound: {result.OutputTail.Length}");
    }

    // ---- budget behavior -------------------------------------------------------------------

    [Fact]
    public async Task The_overall_budget_is_shared_across_build_and_test()
    {
        var calls = 0;
        var session = new RecordingSandboxSession();
        var agent = MakeAgent(budget: TimeSpan.FromSeconds(10),
            elapsed: () => calls++ == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(8));

        var result = await agent.RunAsync(session, MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(TimeSpan.FromSeconds(10), session.Commands[0].Timeout); // full budget first
        Assert.Equal(TimeSpan.FromSeconds(2), session.Commands[1].Timeout);  // remaining only
    }

    [Fact]
    public async Task An_exhausted_budget_never_runs_the_remaining_command()
    {
        var calls = 0;
        var session = new RecordingSandboxSession();
        var agent = MakeAgent(budget: TimeSpan.FromSeconds(10),
            elapsed: () => calls++ == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(10));

        var result = await agent.RunAsync(session, MakeContext());

        Assert.False(result.Passed);
        Assert.Single(session.Commands); // build ran, test never did
        Assert.Contains("budget was exhausted", result.OutputTail);
    }

    // ---- identity ---------------------------------------------------------------------------

    [Fact]
    public async Task The_result_binds_to_the_requested_candidate_sha()
    {
        var session = new RecordingSandboxSession();

        var result = await MakeAgent().RunAsync(session, MakeContext());

        Assert.True(result.Passed);
        Assert.Equal(CandidateSha, result.CandidateSha);
        Assert.DoesNotContain(CandidateSha, result.OutputTail); // identity is not read from output
    }

    [Fact]
    public async Task An_invalid_context_fails_closed()
    {
        var session = new RecordingSandboxSession();
        var badContext = new TesterRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", "not-a-sha", new string('d', 40)),
            WorkPackageId = "WP-001",
            Attempt = 1,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeAgent().RunAsync(session, badContext));
        Assert.Empty(session.Commands);
    }
}
