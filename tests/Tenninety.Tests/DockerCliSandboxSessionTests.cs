using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Deterministic lifecycle tests for the production <see cref="DockerCliSandboxSession"/>:
/// quiescence requires a final typed inspect proving not-running or absence; stop/kill/inspect
/// failures are never suppressed; timeout/cancellation/truncation terminate the whole
/// container; OOM inspection failure is never silently accepted; run, stop and dispose are
/// serialized; disposal is idempotent and concurrent-safe; no unscoped list call exists.
/// </summary>
public class DockerCliSandboxRuntimeSessionLifecycleTests
{
    private static readonly string ContainerId = new('1', 64);
    private static readonly string ImageId = "sha256:" + new string('a', 64);

    private static string InspectJson(bool running, bool oom = false) =>
        "[{\"Id\":\"" + ContainerId + "\",\"Image\":\"" + ImageId +
        "\",\"State\":{\"Running\":" + (running ? "true" : "false") +
        ",\"Paused\":false,\"OOMKilled\":" + (oom ? "true" : "false") +
        ",\"ExitCode\":0},\"Config\":{},\"HostConfig\":{}}]";

    private static DockerCliResult Ok(string stdout = "") =>
        new(0, stdout, "", TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Err(string stderr = "error") =>
        new(1, "", stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult TimedOut() =>
        new(-1, "", "", TimedOut: true, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Cancelled() =>
        new(-1, "", "", TimedOut: false, Cancelled: true,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Truncated() =>
        new(-1, "", "", TimedOut: false, Cancelled: false,
            OutputTruncated: true, Duration: TimeSpan.FromMilliseconds(1));

    private static SandboxCommand Command() =>
        new() { Executable = "true", Arguments = [] };

    private static DockerCliSandboxSession MakeSession(SessionScriptedTransport transport) =>
        new(new DockerCli(transport), ContainerId, SandboxRole.Coder, TimeSpan.FromMinutes(5));

    private static bool Quiescent(ISandboxSession session) => session.WritesQuiescent;

    // ---- quiescence proofs ---------------------------------------------------------

    [Fact]
    public async Task Graceful_stop_plus_inspect_not_running_proves_quiescence()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());                       // stop
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect

        var session = MakeSession(transport);
        await session.StopAsync();

        Assert.Equal(SandboxSessionState.StoppedQuiescent, session.Info.State);
        Assert.True(Quiescent(session));
        Assert.Equal(["stop", "--time", "10", ContainerId], transport.Invocations[0].Arguments);
        Assert.Equal(["inspect", ContainerId], transport.Invocations[1].Arguments);
        transport.AssertNoUnscopedListCall();
    }

    [Fact]
    public async Task Stop_command_succeeds_but_inspect_failure_forces_kill_then_unconfirmed()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());                 // stop "succeeds" — not proof by itself
        transport.Enqueue(_ => Err("inspect boom"));  // first inspect fails
        transport.Enqueue(_ => Ok());                 // kill
        transport.Enqueue(_ => Err("inspect boom"));  // second inspect also fails

        var session = MakeSession(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());

        Assert.Contains("cannot confirm container quiescence", ex.Message);
        Assert.Equal(SandboxSessionState.StoppedUnconfirmed, session.Info.State);
        Assert.False(Quiescent(session));
        Assert.Equal(4, transport.Invocations.Count); // stop, inspect, kill, inspect
    }

    [Fact]
    public async Task Stop_succeeds_but_inspect_still_running_falls_back_to_kill_then_quiescent()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());                          // stop
        transport.Enqueue(_ => Ok(InspectJson(running: true))); // still running
        transport.Enqueue(_ => Ok());                          // kill
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // proof

        var session = MakeSession(transport);
        await session.StopAsync();

        Assert.Equal(SandboxSessionState.StoppedQuiescent, session.Info.State);
        Assert.True(Quiescent(session));
    }

    [Fact]
    public async Task Stop_fails_kill_succeeds_inspect_false_proves_quiescence()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Err("stop failed"));
        transport.Enqueue(_ => Ok(InspectJson(running: true)));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));

        var session = MakeSession(transport);
        await session.StopAsync();

        Assert.Equal(SandboxSessionState.StoppedQuiescent, session.Info.State);
        Assert.True(Quiescent(session));
    }

    [Fact]
    public async Task Stop_and_kill_both_failing_leave_the_session_unconfirmed()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Err("stop failed"));
        transport.Enqueue(_ => Ok(InspectJson(running: true)));
        transport.Enqueue(_ => Err("kill failed"));
        transport.Enqueue(_ => Ok(InspectJson(running: true)));

        var session = MakeSession(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());

        Assert.Contains("kill failed", ex.Message);
        Assert.Contains("still running", ex.Message);
        Assert.Equal(SandboxSessionState.StoppedUnconfirmed, session.Info.State);
        Assert.False(Quiescent(session));
    }

    [Fact]
    public async Task Malformed_final_inspect_never_yields_quiescence()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok("[{\"Id\":\"garbage\"}")); // malformed
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok("[{\"Id\":\"garbage\"}")); // malformed again

        var session = MakeSession(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());

        Assert.Equal(SandboxSessionState.StoppedUnconfirmed, session.Info.State);
        Assert.False(Quiescent(session));
    }

    [Fact]
    public async Task Positively_proven_absence_during_stop_proves_quiescence()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Err("Error: No such container: x"));  // stop: already gone
        transport.Enqueue(_ => Err("Error: No such object: x"));     // inspect: absent

        var session = MakeSession(transport);
        await session.StopAsync();

        Assert.Equal(SandboxSessionState.StoppedQuiescent, session.Info.State);
        Assert.True(Quiescent(session));
    }

    [Fact]
    public async Task Stop_is_idempotent_once_quiescent()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));

        var session = MakeSession(transport);
        await session.StopAsync();
        await session.StopAsync(); // second call returns without any docker call

        Assert.Equal(2, transport.Invocations.Count);
        Assert.Equal(SandboxSessionState.StoppedQuiescent, session.Info.State);
    }

    // ---- termination of the whole container -------------------------------------------

    [Fact]
    public async Task Exec_timeout_terminates_the_whole_container()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => TimedOut());                    // exec
        transport.Enqueue(_ => Ok());                          // kill
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // proof

        var session = MakeSession(transport);
        var result = await session.RunAsync(Command());

        Assert.True(result.TimedOut);
        Assert.False(result.Succeeded);
        Assert.Equal(SandboxSessionState.Failed, session.Info.State);
        Assert.Equal(["kill", ContainerId], transport.Invocations[1].Arguments);
        Assert.Equal(["inspect", ContainerId], transport.Invocations[2].Arguments);
    }

    [Fact]
    public async Task Exec_cancellation_terminates_the_whole_container()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Cancelled());
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));

        var session = MakeSession(transport);
        var result = await session.RunAsync(Command(), new CancellationTokenSource().Token);

        Assert.True(result.Cancelled);
        Assert.False(result.Succeeded);
        Assert.Equal(SandboxSessionState.Failed, session.Info.State);
    }

    [Fact]
    public async Task Output_truncation_terminates_the_whole_container()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Truncated());
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));

        var session = MakeSession(transport);
        var result = await session.RunAsync(Command());

        Assert.True(result.OutputTruncated);
        Assert.False(result.Succeeded);
        Assert.Equal(SandboxSessionState.Failed, session.Info.State);
    }

    [Fact]
    public async Task Failed_termination_is_surfaced_not_suppressed_and_leaves_the_session_unrunnable()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => TimedOut());                      // exec
        transport.Enqueue(_ => Err("kill failed"));              // kill
        transport.Enqueue(_ => Ok(InspectJson(running: true)));  // still running

        var session = MakeSession(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunAsync(Command()));

        Assert.Contains("could not be proven terminated", ex.Message);
        Assert.Contains("kill failed", ex.Message);
        // The session must never be runnable again and must not claim quiescence.
        Assert.Equal(SandboxSessionState.StoppedUnconfirmed, session.Info.State);
        Assert.False(Quiescent(session));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(Command()));
    }

    [Fact]
    public async Task Unprovable_termination_with_failing_inspect_leaves_the_session_unrunnable()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Cancelled());                    // exec
        transport.Enqueue(_ => Err("kill failed"));             // kill
        transport.Enqueue(_ => Err("inspect exploded"));        // proof fails

        var session = MakeSession(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunAsync(Command(), new CancellationTokenSource().Token));

        Assert.Contains("could not be proven terminated", ex.Message);
        Assert.Equal(SandboxSessionState.StoppedUnconfirmed, session.Info.State);
        Assert.False(Quiescent(session));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(Command()));
    }

    [Fact]
    public async Task Proven_termination_records_failed_and_rejects_further_commands()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Truncated());                     // exec
        transport.Enqueue(_ => Ok());                            // kill
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // proof

        var session = MakeSession(transport);
        var result = await session.RunAsync(Command());

        Assert.True(result.OutputTruncated);
        Assert.Equal(SandboxSessionState.Failed, session.Info.State);
        Assert.False(Quiescent(session)); // Failed is not quiescence
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(Command()));
    }

    [Fact]
    public async Task Session_never_returns_to_running_after_termination()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => TimedOut());
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));

        var session = MakeSession(transport);
        await session.RunAsync(Command());

        Assert.NotEqual(SandboxSessionState.Running, session.Info.State);
        Assert.Equal(SandboxSessionState.Failed, session.Info.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(Command()));
    }

    [Fact]
    public async Task Commands_are_rejected_after_termination()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => TimedOut());
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));

        var session = MakeSession(transport);
        await session.RunAsync(Command());

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(Command()));
    }

    // ---- OOM ---------------------------------------------------------------------------

    [Fact]
    public async Task Oom_state_is_reported_true_after_a_nonzero_exit()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => new DockerCliResult(137, "", "killed", TimedOut: false,
            Cancelled: false, OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)));
        transport.Enqueue(_ => Ok(InspectJson(running: true, oom: true)));

        var session = MakeSession(transport);
        var result = await session.RunAsync(Command());

        Assert.True(result.OomKilled);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Oom_inspection_failure_is_never_silently_accepted_as_false()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => new DockerCliResult(1, "", "boom", TimedOut: false,
            Cancelled: false, OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)));
        transport.Enqueue(_ => Err("inspect exploded"));

        var session = MakeSession(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunAsync(Command()));

        Assert.Contains("OOM state", ex.Message);
    }

    // ---- serialization --------------------------------------------------------------------

    [Fact]
    public async Task Run_and_stop_are_serialized_through_the_session()
    {
        var transport = new SessionScriptedTransport();
        var execStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExec = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Enqueue(async _ =>
        {
            execStarted.TrySetResult();
            await releaseExec.Task;
            return TimedOut();
        });
        transport.Enqueue(_ => Ok());                            // kill (run termination)
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect (run termination)
        transport.Enqueue(_ => Ok());                            // stop
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect

        var session = MakeSession(transport);
        var runTask = session.RunAsync(Command());
        await execStarted.Task;

        var stopTask = session.StopAsync();
        Assert.False(stopTask.IsCompleted); // stop cannot run while the exec holds the lock

        releaseExec.TrySetResult();
        await Task.WhenAll(runTask, stopTask);

        // The stop calls strictly follow the run calls in the recorded order.
        var firstStopIndex = transport.Invocations.FindIndex(i => i.Arguments[0] == "stop");
        var lastExecIndex = transport.Invocations.FindLastIndex(i => i.Arguments[0] == "exec");
        Assert.True(firstStopIndex > lastExecIndex);
    }

    [Fact]
    public async Task Run_and_dispose_are_serialized_through_the_session()
    {
        var transport = new SessionScriptedTransport();
        var execStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExec = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Enqueue(async _ =>
        {
            execStarted.TrySetResult();
            await releaseExec.Task;
            return Ok();
        });
        transport.Enqueue(_ => Ok());                            // stop (dispose)
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect (dispose)
        transport.Enqueue(_ => Ok());                            // remove (dispose)
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence confirmation

        var session = MakeSession(transport);
        var runTask = session.RunAsync(Command());
        await execStarted.Task;

        var disposeTask = session.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);

        releaseExec.TrySetResult();
        await Task.WhenAll(runTask, disposeTask);

        Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
        var firstStopIndex = transport.Invocations.FindIndex(i => i.Arguments[0] == "stop");
        var lastExecIndex = transport.Invocations.FindLastIndex(i => i.Arguments[0] == "exec");
        Assert.True(firstStopIndex > lastExecIndex);
    }

    // ---- disposal ---------------------------------------------------------------------------

    [Fact]
    public async Task Repeated_disposal_awaits_the_same_cleanup_and_runs_it_once()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());                            // stop
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect
        transport.Enqueue(_ => Ok());                            // remove
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence confirmation

        var session = MakeSession(transport);
        await session.DisposeAsync();
        await session.DisposeAsync(); // second call awaits the recorded outcome

        Assert.Equal(4, transport.Invocations.Count);
        Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
    }

    [Fact]
    public async Task Concurrent_disposal_awaits_one_shared_cleanup()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(InspectJson(running: false)));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence confirmation

        var session = MakeSession(transport);
        await Task.WhenAll(
            session.DisposeAsync().AsTask(),
            session.DisposeAsync().AsTask(),
            session.DisposeAsync().AsTask());

        Assert.Equal(4, transport.Invocations.Count);
        Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
    }

    [Fact]
    public async Task Removal_failure_is_surfaced_and_the_session_is_not_marked_disposed()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());                            // stop
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect
        transport.Enqueue(_ => Err("rm failed"));                // remove
        transport.Enqueue(_ => Err("daemon gone"));              // absence check fails too

        var session = MakeSession(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.DisposeAsync().AsTask());

        Assert.Contains("cleanup did not fully succeed", ex.Message);
        Assert.Contains("rm failed", ex.Message);
        Assert.NotEqual(SandboxSessionState.Disposed, session.Info.State);
    }

    [Fact]
    public async Task Already_absent_container_is_idempotent_disposal_success()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Err("Error: No such container: x")); // stop: already gone
        transport.Enqueue(_ => Err("Error: No such object: x"));    // inspect: positively absent
        transport.Enqueue(_ => Err("Error: No such container: x")); // rm: already gone
        transport.Enqueue(_ => Err("Error: No such object: x"));    // absence verified

        var session = MakeSession(transport);
        await session.DisposeAsync();

        Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
        Assert.Equal(4, transport.Invocations.Count);
    }

    [Fact]
    public async Task Dispose_after_a_proven_stop_skips_the_second_stop()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());                            // stop (explicit)
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect
        transport.Enqueue(_ => Ok());                            // remove (dispose)
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence confirmation

        var session = MakeSession(transport);
        await session.StopAsync();
        await session.DisposeAsync();

        // Stop ran once during StopAsync; dispose only removes and confirms absence.
        Assert.Equal(4, transport.Invocations.Count);
        Assert.Equal(["rm", "--force", ContainerId], transport.Invocations[2].Arguments);
        Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
    }

    [Fact]
    public async Task No_unscoped_container_list_is_ever_used_for_cleanup()
    {
        var transport = new SessionScriptedTransport();
        transport.Enqueue(_ => Ok());                            // stop
        transport.Enqueue(_ => Ok(InspectJson(running: false))); // inspect
        transport.Enqueue(_ => Ok());                            // remove
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence confirmation

        var session = MakeSession(transport);
        await session.DisposeAsync();
        transport.AssertNoUnscopedListCall();
    }

    [Fact]
    public async Task Session_info_carries_no_host_path_or_container_name()
    {
        var transport = new SessionScriptedTransport();
        var session = MakeSession(transport);
        var json = System.Text.Json.JsonSerializer.Serialize(session.Info);
        Assert.Equal(ContainerId, session.Info.ContainerId);
        Assert.Equal("/workspace", session.Info.ContainerWorkspacePath);
        Assert.DoesNotContain("workspace-path", json);
        Assert.DoesNotContain("name", json);
    }
}

/// <summary>Session-scoped scripted transport: async per-call handlers with call recording
/// and an unscoped-list sentinel assertion.</summary>
public sealed class SessionScriptedTransport : IDockerCliTransport
{
    private readonly Queue<Func<DockerCliInvocation, Task<DockerCliResult>>> _handlers = new();
    private readonly object _gate = new();
    public readonly List<DockerCliInvocation> Invocations = new();

    public void Enqueue(Func<DockerCliInvocation, Task<DockerCliResult>> handler) => _handlers.Enqueue(handler);

    /// <summary>Sync convenience overload.</summary>
    public void Enqueue(Func<DockerCliInvocation, DockerCliResult> handler) =>
        _handlers.Enqueue(inv => Task.FromResult(handler(inv)));

    public Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default)
    {
        lock (_gate) Invocations.Add(invocation);
        if (_handlers.Count == 0)
            throw new InvalidOperationException(
                "no scripted result left: the session performed an unexpected docker call " +
                "for '" + invocation.Arguments[0] + "'.");
        return _handlers.Dequeue()(invocation);
    }

    public void AssertNoUnscopedListCall()
    {
        lock (_gate)
            Assert.All(Invocations, invocation =>
                Assert.NotEqual("ps", invocation.Arguments[0]));
    }
}
