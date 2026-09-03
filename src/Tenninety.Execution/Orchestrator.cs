using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Core.Validation;
using Tenninety.Execution.Sandbox;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Execution;

public enum OrchestratorExit
{
    Completed,
    Deadlocked,
    Paused,
    Stopped,
    Cancelled,
}

/// <summary>
/// Serial queue conductor (Part I.2 principle 3): selects ready WPs in dependency order and drives
/// the ExecutionEngine. Architecture keeps a worker-count knob for future parallelism.
/// </summary>
public sealed class Orchestrator
{
    private readonly IGitService _git;
    private readonly Plan _plan;
    private readonly RuntimeState _state;
    private readonly TenNinetyConfig _config;
    private readonly IFrontierClient _frontier;
    private readonly AgentFactory _agents;
    private readonly StateStore _stateStore;
    private readonly AuditLog _audit;
    private readonly Action<string>? _log;

    /// <summary>Deterministic fake-first seam; production always uses scoped Docker recovery.</summary>
    internal Func<CancellationToken, Task<SandboxRecoveryInfo>>? RecoveryOverride { get; set; }

    public Orchestrator(
        IGitService git, Plan plan, RuntimeState state, TenNinetyConfig config,
        IFrontierClient frontier, StateStore stateStore, AuditLog audit,
        Action<string>? log = null)
    {
        _git = git;
        _plan = plan;
        _state = state;
        _config = config;
        _frontier = frontier;
        _agents = new AgentFactory(config);
        _stateStore = stateStore;
        _audit = audit;
        _log = log;

        if (config.ExecutionMode != "serial")
            throw new NotSupportedException("v3.2 supports serial execution; parallel mode is planned.");

        // Restart recovery: state.json is the single source of truth for progress. Hydrate the
        // freshly loaded plan with persisted queue statuses so an interrupted-and-restarted run
        // never re-executes completed work. Only TERMINAL statuses are trusted from disk – a
        // stale ACTIVE entry (hard crash mid-job) falls back to PENDING so the job can resume.
        foreach (var wp in plan.WorkPackages)
        {
            if (!state.QueueStatus.TryGetValue(wp.Id, out var status)) continue;
            if (status is TenNinety.WpStatus.Done
                     or TenNinety.WpStatus.Blocked
                     or TenNinety.WpStatus.Cancelled)
                wp.Status = status;
        }
    }

    public async Task<OrchestratorExit> RunAsync(CancellationToken ct)
    {
        using var daemonLock = DaemonLock.Acquire(_git.RepoPath);
        if (_git.CurrentBranch() != TenNinety.MainBranch)
            throw new InvalidOperationException(
                $"the framework must start from branch '{TenNinety.MainBranch}', not '{_git.CurrentBranch()}'.");
        var runtimeIgnore = $"{TenNinety.StateDir}/.gitignore";
        if (!_git.IsPathClean(runtimeIgnore))
            throw new InvalidOperationException(
                $"{runtimeIgnore} has uncommitted edits; commit or restore it before runtime migration.");
        if (RuntimeGitignoreMigration.Ensure(_git.RepoPath))
            _git.CommitPaths(
                [runtimeIgnore],
                "tenninety: update runtime ignores");
        if (!_git.IsClean())
            throw new InvalidOperationException(
                "runtime-ignore migration left the working tree dirty; commit .tenninety/.gitignore and retry.");
        await RecoverSandboxResourcesAsync(ct);
        _audit.Append("DAEMON_STARTED", detail: $"mode={_config.ExecutionMode} provider={_config.ProviderMode}");
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Control channel: external commands latch request FILES; the daemon consumes
                // them here, so supervision works across process boundaries (external review M3).
                var (pauseRequested, stopRequested) = ExecutionControl.ConsumeFlags(_git.RepoPath);
                if (stopRequested) _state.StopRequested = true;
                if (pauseRequested && !_state.Paused) { _state.Paused = true; _audit.Append("PAUSED", detail: "requested while idle"); }

                if (_state.StopRequested)
                {
                    _audit.Append("DAEMON_STOPPED");
                    return OrchestratorExit.Stopped;
                }
                if (_state.Paused)
                {
                    // Exit rather than idle: resume == fix cause, then 'tenninety start' again.
                    Persist();
                    _audit.Append("PAUSED", detail: "daemon exits while paused");
                    return OrchestratorExit.Paused;
                }

                var wp = SelectNextReady();
                if (wp is null)
                    return AllSuccessfullyTerminal() ? OrchestratorExit.Completed : ReportDeadlock();

                if (!_git.IsClean())
                    throw new InvalidOperationException(
                        "working tree is not clean; commit or stash before running the orchestrator.");

                var engine = new ExecutionEngine(
                    _git, _config, _frontier,
                    _agents.CreateCoder(_git, daemonLock, _log),
                    _agents.CreateReviewer(_git, _log),
                    _agents.CreateTester(_git, _log), _stateStore, _audit,
                    _plan.GlobalContext, _log);

                var outcome = await engine.ExecuteWpAsync(wp, _state, ct);
                switch (outcome)
                {
                    case WpOutcome.Paused:
                        return OrchestratorExit.Paused;
                    case WpOutcome.Stopped:
                        return OrchestratorExit.Stopped;
                    case WpOutcome.Blocked:
                        NotifyActionRequired(wp);
                        break;
                    case WpOutcome.Done:
                        break;
                }
            }
        }
        finally
        {
            Persist();
            _audit.Append("DAEMON_EXITED", detail: $"queue_done={CountByStatus(TenNinety.WpStatus.Done)}");
        }
    }

    /// <summary>
    /// Lowest-id WP whose dependencies are all DONE and which is still PENDING.
    /// CONFLICT-flagged packages (blueprint v3.2 Enterprise ambiguity protocol) are never
    /// scheduled: they carry no directives and require human resolution via a pivot REWORK.
    /// </summary>
    public WorkPackage? SelectNextReady()
    {
        var byId = _plan.WorkPackages.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        return _plan.WorkPackages
            .Where(w => w.Status == TenNinety.WpStatus.Pending && !WpMarkers.IsConflict(w))
            .Where(w => w.Dependencies.All(d =>
                byId.TryGetValue(d, out var dep) && dep.Status == TenNinety.WpStatus.Done))
            .OrderBy(w => PlanValidator.IdOrder(w.Id))
            .ThenBy(w => w.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public bool AllTerminal() => _plan.WorkPackages.All(w => w.IsTerminal);

    private bool AllSuccessfullyTerminal() => _plan.WorkPackages.All(w =>
        w.Status is TenNinety.WpStatus.Done or TenNinety.WpStatus.Cancelled);

    public int CountByStatus(string status) =>
        _plan.WorkPackages.Count(w => w.Status.Equals(status, StringComparison.OrdinalIgnoreCase));

    private OrchestratorExit ReportDeadlock()
    {
        var unresolved = _plan.WorkPackages.Where(w => !w.IsTerminal).ToList();
        var blocked = _plan.WorkPackages
            .Where(w => w.Status == TenNinety.WpStatus.Blocked)
            .ToList();
        var conflicts = unresolved.Where(WpMarkers.IsConflict).ToList();
        var others = unresolved.Except(conflicts).ToList();

        var detail = new List<string>();
        if (blocked.Count > 0)
            detail.Add($"BLOCKED WPs requiring human action: {string.Join(", ", blocked.Select(w => w.Id))}");
        if (conflicts.Count > 0)
            detail.Add($"CONFLICT WPs awaiting human resolution: {string.Join(", ", conflicts.Select(w => w.Id))}");
        if (others.Count > 0)
            detail.Add($"non-terminal: {string.Join(", ", others.Select(w => $"{w.Id} (deps: {string.Join(",", w.Dependencies)})"))}");

        var message = string.Join(" | ", detail);
        _audit.Append("QUEUE_DEADLOCKED", detail: message);
        _log?.Invoke($"Queue deadlocked — no executable WP is ready. {message}");
        return OrchestratorExit.Deadlocked;
    }

    private void NotifyActionRequired(WorkPackage wp) =>
        _log?.Invoke($"ACTION REQUIRED: '{wp.Id}' is BLOCKED after {_config.MaxTotalAttempts} attempts.");

    public void Pause()
    {
        ExecutionControl.SetPause(_git.RepoPath);
        _audit.Append("PAUSED_REQUESTED");
        _log?.Invoke("pause requested");
    }

    public void Resume()
    {
        ExecutionControl.ClearAll(_git.RepoPath);
        _state.Paused = false;
        _state.StopRequested = false;
        Persist();
        _audit.Append("RESUMED");
        _log?.Invoke("resumed");
    }

    public void RequestStop()
    {
        ExecutionControl.SetStop(_git.RepoPath);
        _log?.Invoke("stop requested – daemon will halt at the next safe point");
    }

    private void Persist()
    {
        SyncQueueStatuses();
        _stateStore.Save(_state);
    }

    private async Task RecoverSandboxResourcesAsync(CancellationToken ct)
    {
        SandboxRecoveryInfo recovery;
        try
        {
            recovery = RecoveryOverride is { } recover
                ? await recover(ct)
                : await new SandboxRecoveryService(_git, _config).RecoverAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            recovery = new SandboxRecoveryInfo
            {
                Status = "quarantined",
                LastRunUtc = DateTimeOffset.UtcNow.ToString("O"),
                Quarantined = ["recovery-inventory"],
                Detail = "Scoped sandbox inventory or cleanup failed (" +
                         ex.GetType().Name + "); execution is refused.",
            };
        }

        _state.SandboxRecovery = recovery;
        Persist();
        _audit.Append(
            recovery.Status == "quarantined"
                ? "SANDBOX_RECOVERY_QUARANTINED"
                : "SANDBOX_RECOVERY_COMPLETED",
            detail: $"status={recovery.Status} containers=" +
                    $"{recovery.ContainersRemoved}/{recovery.ContainersFound} workspaces=" +
                    $"{recovery.WorkspacesRemoved}/{recovery.WorkspacesFound} " +
                    $"quarantined={recovery.Quarantined.Count}");
        if (recovery.Status == "quarantined" || recovery.Quarantined.Count > 0)
            throw new InvalidOperationException(
                "sandbox startup recovery did not prove complete cleanup; " +
                "execution is refused until the scoped quarantine is resolved.");
    }

    private void SyncQueueStatuses()
    {
        foreach (var wp in _plan.WorkPackages)
            _state.QueueStatus[wp.Id] = wp.Status;
    }
}
