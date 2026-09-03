namespace Tenninety.Execution.Sandbox;

/// <summary>
/// One live Docker sandbox session backed by the typed <see cref="DockerCli"/> adapter.
/// RunAsync, StopAsync, and DisposeAsync are serialized through one semaphore so state
/// transitions are monotonic and cannot race.
///
/// Quiescence contract: a successful `docker stop` command is NOT proof. StoppedQuiescent is
/// reached only when a typed inspect positively proves the container is not running, or a
/// typed operation positively proves it is absent. Any other outcome leaves the session in
/// StoppedUnconfirmed and throws a bounded structured failure. No host path is ever stored
/// or exposed: <see cref="Info"/> carries only the container id, role, and state.
/// </summary>
public sealed class DockerCliSandboxSession : ISandboxSession
{
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(10);

    private readonly DockerCli _cli;
    private readonly string _containerId;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _disposeGate = new();
    private SandboxSessionState _state = SandboxSessionState.Running;
    private Task? _disposeTask;

    /// <summary>Constructed only after the runtime proved the container is running.</summary>
    internal DockerCliSandboxSession(DockerCli cli, string containerId, SandboxRole role, TimeSpan sessionTimeout)
    {
        _cli = cli;
        _containerId = containerId;
        SessionTimeout = sessionTimeout;
        Info = new SandboxSessionInfo(containerId, role, _state);
    }

    public SandboxSessionInfo Info { get; private set; }
    public TimeSpan SessionTimeout { get; }

    private SandboxSessionState State
    {
        get => _state;
        set
        {
            _state = value;
            Info = Info with { State = value };
        }
    }

    public async Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_state is not SandboxSessionState.Running)
                throw new InvalidOperationException(
                    $"cannot execute a command: the session is {_state}.");

            command.Validate(SessionTimeout);
            var request = DockerExecRequest.FromCommand(
                _containerId, command, command.Timeout ?? SessionTimeout);

            var result = await _cli.ExecAsync(request, ct);

            if (result.TimedOut || result.Cancelled || result.OutputTruncated)
            {
                // Timeout, caller cancellation, or output truncation terminate the WHOLE
                // container — killing only the local docker exec client is insufficient.
                // The session leaves Running IMMEDIATELY and can never return to it: even
                // when termination cannot be proven, no later RunAsync may execute.
                State = SandboxSessionState.Stopping;
                try
                {
                    await TerminateContainerAsync();
                    State = SandboxSessionState.Failed;
                }
                catch (Exception terminationFailure)
                {
                    State = SandboxSessionState.StoppedUnconfirmed;
                    throw new InvalidOperationException(
                        "command timed out, was cancelled or its output was truncated, and the " +
                        "container could not be proven terminated: " + terminationFailure.Message,
                        terminationFailure);
                }
                return result;
            }

            if (result.ExitCode != 0)
            {
                // Inspect OOM state after abnormal/nonzero execution. A failed or malformed
                // inspection is never silently converted into OomKilled=false.
                DockerContainerState state;
                try
                {
                    state = await _cli.InspectContainerAsync(_containerId, ct: CancellationToken.None);
                }                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "could not verify OOM state after a nonzero exit: " + ex.Message, ex);
                }
                if (state.OomKilled)
                    result = result with { OomKilled = true };
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await StopCoreAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Shared stop/proof sequence (caller holds the semaphore).</summary>
    private async Task StopCoreAsync(CancellationToken ct)
    {
        if (_state == SandboxSessionState.StoppedQuiescent)
            return;
        if (_state == SandboxSessionState.Disposed)
            throw new InvalidOperationException("the session is already disposed.");

        // Monotonic transition to Stopping (once, from live states). A retry after
        // StoppedUnconfirmed/Failed keeps the state and may only improve the outcome.
        if (_state is SandboxSessionState.Created or SandboxSessionState.Running or SandboxSessionState.Stopping)
            State = SandboxSessionState.Stopping;

        Exception? stopError = null;
        try
        {
            await _cli.StopContainerAsync(_containerId, StopGracePeriod, ct);
        }
        catch (Exception ex)
        {
            stopError = ex;
        }

        // First proof: successful stop alone is never accepted as evidence.
        var proof = await ProbeAsync();
        if (proof is Proof.Running or Proof.Failed)
        {
            Exception? killError = null;
            try
            {
                await _cli.KillContainerAsync(_containerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                killError = ex;
            }

            proof = await ProbeAsync();
            if (proof is Proof.Running or Proof.Failed)
            {
                State = SandboxSessionState.StoppedUnconfirmed;
                throw new InvalidOperationException(
                    "cannot confirm container quiescence" +
                    (stopError is null ? "" : "; graceful stop failed: " + stopError.Message) +
                    (killError is null ? "" : "; kill failed: " + killError.Message) +
                    (proof == Proof.Failed
                        ? "; inspect failed"
                        : "; the container is still running") +
                    ". Extraction is forbidden and the workspace is quarantined.");
            }
        }

        State = SandboxSessionState.StoppedQuiescent;
    }

    private async Task<Proof> ProbeAsync()
    {
        try
        {
            var state = await _cli.TryInspectContainerAsync(_containerId, CancellationToken.None);
            if (state is null) return Proof.Absent;                 // positively proven absent
            return state.Running ? Proof.Running : Proof.NotRunning; // positively proven not running
        }
        catch
        {
            return Proof.Failed; // inspect failed: no quiescence claim is possible
        }
    }

    private enum Proof { Absent, NotRunning, Running, Failed }

    /// <summary>
    /// Idempotent and concurrency-safe disposal: every caller awaits the same cleanup task.
    /// Stop/proof runs first, then removal with positive absence verification; a failure to
    /// prove removal or absence is surfaced instead of converted to success. The session
    /// ends in Disposed only after the lifecycle outcome is recorded. The semaphore is never
    /// disposed while other callers may still wait on it.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= RunDisposalAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task RunDisposalAsync()
    {
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            var failures = new List<string>();

            // 1. Stop and prove quiescence or absence first (unless already proven).
            if (_state != SandboxSessionState.StoppedQuiescent)
            {
                try
                {
                    await StopCoreAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    failures.Add("stop: " + ex.Message);
                }
            }

            // 2. Remove; positively establish already-absent status through typed operations.
            try
            {
                // True = removed, false = positively absent; both end the resource's life.
                await _cli.RemoveContainerAsync(_containerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                failures.Add("remove: " + ex.Message);
            }

            if (failures.Count > 0)
            {
                // Lifecycle outcome recorded; state stays non-disposed (unproven cleanup).
                throw new InvalidOperationException(
                    "sandbox cleanup did not fully succeed and a scoped container may remain: " +
                    string.Join("; ", failures));
            }

            State = SandboxSessionState.Disposed;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Kills the whole container and positively proves it is gone or not running.
    /// Throws a bounded structured failure when termination cannot be proven.</summary>
    private async Task TerminateContainerAsync()
    {
        Exception? killError = null;
        try
        {
            await _cli.KillContainerAsync(_containerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            killError = ex;
        }

        try
        {
            var state = await _cli.TryInspectContainerAsync(_containerId, CancellationToken.None);
            if (state is null || !state.Running)
                return; // positively proven absent or not running
        }
        catch { /* fall through to combined failure */ }

        throw new InvalidOperationException(
            "failed to terminate the container after timeout/cancellation/output truncation" +
            (killError is null ? "" : "; kill failed: " + killError.Message) +
            "; the termination state could not be proven.");
    }
}
