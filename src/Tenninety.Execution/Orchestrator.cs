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
    private readonly PromotionTransactionStore _promotionStore;
    private readonly AuditLog _audit;
    private readonly Action<string>? _log;
    private DaemonLockLease? _initialDaemonLock;

    /// <summary>Deterministic fake-first seam; production always uses scoped Docker recovery.</summary>
    internal Func<CancellationToken, Task<SandboxRecoveryInfo>>? RecoveryOverride { get; set; }

    public Orchestrator(
        IGitService git, Plan plan, RuntimeState state, TenNinetyConfig config,
        IFrontierClient frontier, StateStore stateStore, AuditLog audit,
        Action<string>? log = null, DaemonLockLease? initialDaemonLock = null)
    {
        _git = git;
        _plan = plan;
        _state = state;
        _config = config;
        _frontier = frontier;
        _agents = new AgentFactory(config);
        _stateStore = stateStore;
        _promotionStore = new PromotionTransactionStore(
            Path.Combine(git.RepoPath, TenNinety.StateDir, TenNinety.PromotionFile));
        _audit = audit;
        _log = log;
        initialDaemonLock?.ThrowIfNotLiveFor(git.RepoPath);
        _initialDaemonLock = initialDaemonLock;

        if (config.ExecutionMode != "serial")
            throw new NotSupportedException("serial execution is supported; parallel mode is planned.");

        // Restart recovery: state.json is the single source of truth for progress. Hydrate the
        // freshly loaded plan with persisted queue statuses so an interrupted-and-restarted run
        // never re-executes completed work. Only TERMINAL statuses are trusted from disk – a
        // stale ACTIVE entry (hard crash mid-job) falls back to PENDING so the job can resume.
        HydrateTerminalStatuses(plan, state);
    }

    public async Task<OrchestratorExit> RunAsync(CancellationToken ct)
    {
        // The CLI acquires the first lease before reading plan/state and transfers it here,
        // closing the stale-snapshot race. Direct callers and later TUI resumes acquire here.
        using var daemonLock = Interlocked.Exchange(ref _initialDaemonLock, null) ??
                               DaemonLock.Acquire(_git.RepoPath);
        return await RunUnderLeaseAsync(daemonLock, ct);
    }

    /// <summary>Atomically resumes a stopped dashboard: the daemon lease is acquired before
    /// persisted plan/state or control markers are touched, refreshed objects remain the same
    /// instances observed by the TUI, and the lease is retained through startup and execution.</summary>
    public async Task<OrchestratorExit> ResumeAsync(CancellationToken ct)
    {
        using var daemonLock = DaemonLock.Acquire(_git.RepoPath);
        RefreshWorkspaceUnderLease(daemonLock);
        ExecutionControl.ClearAll(_git.RepoPath);
        _state.Paused = false;
        _state.StopRequested = false;
        // Do not synchronize queue statuses before recovery. A persisted ACTIVE/current_wp/
        // execution_id tuple is exact interrupted-execution evidence used during startup.
        _stateStore.Save(_state);
        _audit.Append("RESUMED");
        Log("resumed");
        return await RunUnderLeaseAsync(daemonLock, ct);
    }

    private async Task<OrchestratorExit> RunUnderLeaseAsync(
        DaemonLockLease daemonLock, CancellationToken ct)
    {
        daemonLock.ThrowIfNotLiveFor(_git.RepoPath);
        // Recovery owns the daemon lock but does not require a clean/main checkout. A crashed
        // job commonly leaves its work branch selected, and scoped Docker resources must be
        // cleaned before any branch rejection can stop startup.
        await RecoverSandboxResourcesAsync(ct);
        RefusePromotionJournalInOtherWorktree();
        RecoverPromotionTransaction();
        ReconcileInterruptedWorkBranch();
        if (_git.CurrentBranch() != TenNinety.MainBranch)
            throw new InvalidOperationException(
                $"the framework must start from branch '{TenNinety.MainBranch}', not '{_git.CurrentBranch()}'.");
        var runtimeIgnore = $"{TenNinety.StateDir}/.gitignore";
        if (!RuntimeGitignoreMigration.PromotionArtifactsAreIgnored(_git))
        {
            if (!_git.IsPathClean(runtimeIgnore))
                throw new InvalidOperationException(
                    $"{runtimeIgnore} has uncommitted edits; commit or restore it before runtime migration.");
            if (RuntimeGitignoreMigration.Ensure(_git.RepoPath))
                _git.CommitPaths(
                    [runtimeIgnore],
                    "tenninety: update runtime ignores");
        }
        if (!RuntimeGitignoreMigration.PromotionArtifactsAreIgnored(_git))
            throw new InvalidOperationException(
                "promotion recovery journal files are not effectively ignored; remove overriding " +
                "ignore negations or ignore .tenninety/ before starting execution.");
        if (!_git.IsClean())
            throw new InvalidOperationException(
                "runtime-ignore migration left the working tree dirty; commit .tenninety/.gitignore and retry.");
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
    /// CONFLICT-flagged packages (blueprint ambiguity protocol) are never
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
        Log($"Queue deadlocked — no executable WP is ready. {message}");
        return OrchestratorExit.Deadlocked;
    }

    private void NotifyActionRequired(WorkPackage wp) =>
        Log($"ACTION REQUIRED: '{wp.Id}' is BLOCKED after {_config.MaxTotalAttempts} attempts.");

    public void Pause()
    {
        ExecutionControl.SetPause(_git.RepoPath);
        _audit.Append("PAUSED_REQUESTED");
        Log("pause requested");
    }

    /// <summary>Clears completion-race markers only when no other daemon owns the repository.
    /// An old dashboard must never consume requests belonging to a newer active run.</summary>
    public void ClearControlRequestsIfIdle()
    {
        DaemonLockLease daemonLock;
        try
        {
            daemonLock = DaemonLock.Acquire(_git.RepoPath);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is IOException)
        {
            return;
        }

        using (daemonLock)
            ExecutionControl.ClearAll(_git.RepoPath);
    }

    public void RequestStop()
    {
        ExecutionControl.SetStop(_git.RepoPath);
        Log("stop requested – daemon will halt at the next safe point");
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
        // Do not synchronize queue statuses here: before interrupted-branch/promotion
        // reconciliation the freshly loaded plan intentionally maps stale ACTIVE to PENDING.
        // Overwriting the persisted ACTIVE identity would destroy exact recovery evidence.
        _stateStore.Save(_state);
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

    private void RecoverPromotionTransaction()
    {
        var transaction = _promotionStore.Load();
        if (transaction is null) return;

        try
        {
            WorkPackage? wp = null;
            if (transaction.Kind == TenNinety.PromotionKinds.WorkPackage)
            {
                wp = _plan.WorkPackages.SingleOrDefault(candidate =>
                    candidate.Id.Equals(transaction.WorkPackageId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        "the transaction references a work package absent from the current plan.");
                var status = _state.QueueStatus.TryGetValue(wp.Id, out var persisted)
                    ? persisted
                    : wp.Status;
                if (status == TenNinety.WpStatus.Done)
                {
                    if (_state.CurrentWp is not null ||
                        (_state.Attempts.TryGetValue(wp.Id, out var completedAttempt) &&
                         completedAttempt.ExecutionId != transaction.ExecutionId))
                        throw new InvalidOperationException(
                            "saved progress conflicts with the completed promotion execution.");
                }
                else
                {
                    if (status is not (TenNinety.WpStatus.Active or TenNinety.WpStatus.Pending) ||
                        _state.CurrentWp?.Equals(wp.Id, StringComparison.OrdinalIgnoreCase) != true ||
                        !_state.Attempts.TryGetValue(wp.Id, out var attempt) ||
                        attempt.ExecutionId != transaction.ExecutionId)
                        throw new InvalidOperationException(
                            "saved progress does not identify the exact interrupted promotion execution.");
                }
            }

            try
            {
                _git.CompleteSquashPromotion(
                    transaction.Branch,
                    transaction.ExpectedBaseSha,
                    transaction.CandidateSha,
                    transaction.PromotionSha);
            }
            catch (SquashPromotionException ex) when (ex.RecoverySucceeded)
            {
                _audit.Append("PROMOTION_FORWARD_RECOVERED", transaction.WorkPackageId,
                    Core.Security.Sanitizer.SanitizeDiagnostic(ex.Message, 500));
            }

            if (wp is not null)
            {
                wp.Status = TenNinety.WpStatus.Done;
                _state.CurrentWp = null;
                _state.Attempts.Remove(wp.Id);
                _state.QueueStatus[wp.Id] = TenNinety.WpStatus.Done;
                // The transaction remains durable while recovered progress is atomically saved.
                Persist();
            }

            _git.DeleteBranchCompareAndSwap(
                transaction.Branch, transaction.CandidateSha);
            _promotionStore.Complete(transaction);
            try
            {
                _git.ReleaseSquashPromotion(
                    transaction.CandidateSha, transaction.PromotionSha);
            }
            catch (Exception ex)
            {
                _audit.Append("PROMOTION_REF_CLEANUP_REQUIRED", transaction.WorkPackageId,
                    Core.Security.Sanitizer.SanitizeDiagnostic(ex.Message, 500));
            }
            _audit.Append("PROMOTION_RECOVERED", transaction.WorkPackageId,
                $"kind={transaction.Kind} execution={transaction.ExecutionId} " +
                $"promotion={transaction.PromotionSha[..12]}");
        }
        catch (Exception ex)
        {
            _audit.Append("PROMOTION_RECOVERY_REQUIRED", transaction.WorkPackageId,
                $"kind={transaction.Kind} execution={transaction.ExecutionId} " +
                $"failure={ex.GetType().Name}");
            throw new InvalidOperationException(
                "an exact promotion transaction is pending but automatic reconciliation " +
                "could not be proven safe. Scoped sandbox cleanup has completed. Preserve " +
                $"'{transaction.Branch}' and '{TenNinety.PromotionFile}', inspect main and " +
                "the worktree, resolve any external changes, then restart.", ex);
        }
    }

    private void RefusePromotionJournalInOtherWorktree()
    {
        var journals = RuntimeGitignoreMigration.CountPromotionJournalsInOtherWorktrees(_git);
        if (journals > 0)
            throw new InvalidOperationException(
                $"{journals} linked worktree promotion transaction(s) require recovery. " +
                "Run 'tenninety start' from the owning worktree before executing here.");
    }

    private void ReconcileInterruptedWorkBranch()
    {
        var current = _git.SymbolicHeadBranch();
        if (current == TenNinety.MainBranch) return;

        if (current is not null &&
            current.StartsWith(TenNinety.WorkBranchPrefix, StringComparison.Ordinal))
        {
            var id = current[TenNinety.WorkBranchPrefix.Length..];
            var wp = _plan.WorkPackages.SingleOrDefault(candidate =>
                candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            var status = wp is not null && _state.QueueStatus.TryGetValue(wp.Id, out var saved)
                ? saved
                : null;
            var known = wp is not null &&
                        _state.CurrentWp?.Equals(wp.Id, StringComparison.OrdinalIgnoreCase) == true &&
                        status is TenNinety.WpStatus.Active or TenNinety.WpStatus.Pending &&
                        _state.Attempts.ContainsKey(wp.Id) &&
                        _git.BranchExists(current) &&
                        _git.FindCommit(current)?.Sha == _git.HeadSha();
            if (known)
            {
                if (!_git.IsClean())
                    throw new InvalidOperationException(
                        $"interrupted work package '{wp!.Id}' was identified, and scoped sandbox " +
                        $"cleanup completed, but branch '{current}' has uncommitted work. The " +
                        "framework preserved it; inspect and commit or otherwise resolve those " +
                        "changes on the work branch, then restart.");

                _git.CheckoutBranch(TenNinety.MainBranch);
                wp!.Status = TenNinety.WpStatus.Pending;
                _state.CurrentWp = null;
                _state.QueueStatus[wp.Id] = TenNinety.WpStatus.Pending;
                Persist();
                _audit.Append("WORK_BRANCH_RECOVERED", wp.Id,
                    $"branch={current} attempts-preserved=true");
                return;
            }
        }

        var displayed = current ?? "detached HEAD";
        throw new InvalidOperationException(
            $"startup sandbox cleanup completed, but checkout '{displayed}' is not the exact " +
            "interrupted work branch recorded in state.json. It was left untouched; switch to " +
            $"'{TenNinety.MainBranch}' only after preserving and reviewing that work.");
    }

    private void SyncQueueStatuses()
    {
        foreach (var wp in _plan.WorkPackages)
            _state.QueueStatus[wp.Id] = wp.Status;
    }

    private void RefreshWorkspaceUnderLease(DaemonLockLease daemonLock)
    {
        daemonLock.ThrowIfNotLiveFor(_git.RepoPath);
        var refreshedPlan = new PlanStore(Path.Combine(
            _git.RepoPath, TenNinety.StateDir, TenNinety.PlanFile)).Load();
        var validation = PlanValidator.Validate(refreshedPlan);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                "plan.json is invalid: " + string.Join("; ", validation.Errors));
        var refreshedState = _stateStore.Load();

        CopyPlan(refreshedPlan, _plan);
        CopyState(refreshedState, _state);
        HydrateTerminalStatuses(_plan, _state);
    }

    private static void HydrateTerminalStatuses(Plan plan, RuntimeState state)
    {
        foreach (var wp in plan.WorkPackages)
        {
            if (!state.QueueStatus.TryGetValue(wp.Id, out var status)) continue;
            if (status is TenNinety.WpStatus.Done
                     or TenNinety.WpStatus.Blocked
                     or TenNinety.WpStatus.Cancelled)
                wp.Status = status;
        }
    }

    private static void CopyPlan(Plan source, Plan target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.ProjectName = source.ProjectName;
        target.GlobalContext = source.GlobalContext;
        target.ArchitectureMap = source.ArchitectureMap;
        target.WorkPackages = source.WorkPackages;
    }

    private static void CopyState(RuntimeState source, RuntimeState target)
    {
        target.CurrentWp = source.CurrentWp;
        target.ExecutionMode = source.ExecutionMode;
        target.Attempts = source.Attempts;
        target.QueueStatus = source.QueueStatus;
        target.Paused = source.Paused;
        target.StopRequested = source.StopRequested;
        target.SpecHash = source.SpecHash;
        target.SandboxRecovery = source.SandboxRecovery;
    }

    private void Log(string message) => _log?.Invoke(
        Core.Security.Sanitizer.SanitizeDiagnostic(message ?? "", 4000));
}
