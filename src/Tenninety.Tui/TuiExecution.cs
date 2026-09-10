using System.Runtime.CompilerServices;
using Tenninety.Core.Security;
using Tenninety.Execution;

[assembly: InternalsVisibleTo("Tenninety.Tests")]

namespace Tenninety.Tui;

/// <summary>Owns one dashboard's run tasks and observes their actual outcomes, independent of terminal input.</summary>
internal sealed class TuiExecution : IDisposable
{
    private readonly Func<CancellationToken, Task<OrchestratorExit>> _run;
    private readonly Action _pause;
    private readonly Func<CancellationToken, Task<OrchestratorExit>> _resume;
    private readonly Action _stop;
    private readonly Action _clearControl;
    private CancellationTokenSource _cts = new();
    private Task<OrchestratorExit> _task;
    private Task? _observation;
    private bool _failed;
    private int _shutdownRequested;

    public TuiExecution(Func<CancellationToken, Task<OrchestratorExit>> run,
        Action pause, Func<CancellationToken, Task<OrchestratorExit>> resume,
        Action stop, Action? clearControl = null)
    {
        _run = run;
        _pause = pause;
        _resume = resume;
        _stop = stop;
        _clearControl = clearControl ?? (() => { });
        _task = RunAsync(_run);
    }

    public bool IsRunning => !_task.IsCompleted;
    public OrchestratorExit? Exit { get; private set; }
    public string? Banner { get; set; }

    // A retry changes the current status, but must not erase the previous failure diagnostic.
    public string? LastError { get; private set; }

    public string Status => _failed ? "FAILED" : Exit switch
    {
        null => "RUNNING",
        OrchestratorExit.Completed => "COMPLETED",
        OrchestratorExit.Paused => "PAUSED",
        OrchestratorExit.Stopped => "STOPPED",
        OrchestratorExit.Deadlocked => "DEADLOCKED",
        OrchestratorExit.Cancelled => "CANCELLED",
        _ => "FAILED",
    };

    public int ExitCode => _failed ? 1 : Exit switch
    {
        OrchestratorExit.Completed or OrchestratorExit.Paused or OrchestratorExit.Stopped => 0,
        OrchestratorExit.Deadlocked => 4,
        _ => 1,
    };

    public Task ObserveCompletedAsync() => _task.IsCompleted ? ObserveAsync() : Task.CompletedTask;

    public async Task PauseAsync()
    {
        await ObserveCompletedAsync();
        if (!IsRunning) return;
        Banner = "pausing daemon at the next safe point...";
        _pause();
        await ObserveAsync();
        ClearControlRequests();
    }

    public async Task ResumeAsync()
    {
        if (IsRunning) return;
        await ObserveAsync();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        Exit = null;
        _failed = false;
        _observation = null;
        Interlocked.Exchange(ref _shutdownRequested, 0);
        Banner = "resumed";
        _task = RunAsync(_resume);
        await ObserveCompletedAsync();
    }

    public async Task<int> ShutdownAsync(TimeSpan? gracePeriod = null)
    {
        RequestShutdown(gracePeriod);
        await ObserveAsync();
        ClearControlRequests();
        _cts.CancelAfter(Timeout.InfiniteTimeSpan);
        return ExitCode;
    }

    /// <summary>Signal-safe synchronous half of shutdown. It starts the cancellation grace
    /// period immediately, even while the UI is awaiting another operation.</summary>
    public void RequestShutdown(TimeSpan? gracePeriod = null)
    {
        if (!IsRunning || Interlocked.Exchange(ref _shutdownRequested, 1) != 0) return;
        _cts.CancelAfter(gracePeriod ?? TimeSpan.FromSeconds(15));
        try { _stop(); }
        catch (Exception ex)
        {
            RecordFailure(ex);
            _cts.Cancel();
        }
    }

    private async Task<OrchestratorExit> RunAsync(
        Func<CancellationToken, Task<OrchestratorExit>> operation) =>
        await operation(_cts.Token);

    private Task ObserveAsync() => _observation ??= ObserveRunAsync();

    private async Task ObserveRunAsync()
    {
        try { Exit = await _task; }
        catch (OperationCanceledException) when (_task.IsCanceled)
        {
            Exit = OrchestratorExit.Cancelled;
        }
        catch (Exception ex)
        {
            RecordFailure(ex);
        }
        Banner = null;
    }

    private void RecordFailure(Exception ex)
    {
        _failed = true;
        LastError = Sanitizer.SanitizeDiagnostic($"daemon error: {ex.Message}", 4000);
    }

    private void ClearControlRequests()
    {
        if (IsRunning) return;
        try { _clearControl(); }
        catch (Exception ex) { RecordFailure(ex); }
    }

    public void Dispose() => _cts.Dispose();
}
