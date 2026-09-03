using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Testing;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Execution;

public enum WpOutcome
{
    Done,
    Blocked,
    Paused,
    Stopped,
}

/// <summary>
/// The Autonomous Execution Loop (Part II diagram 2): Coder → Reviewer → Tester with accumulated
/// failure context; escalation to the Frontier at attempt 10; BLOCKED at 20 total attempts.
/// </summary>
public sealed class ExecutionEngine
{
    private readonly IGitService _git;
    private readonly TenNinetyConfig _config;
    private readonly IFrontierClient _frontier;
    private readonly ICoderAgent _coder;
    private readonly IReviewerAgent _reviewer;
    private readonly ITesterAgent _tester;
    private readonly StateStore _stateStore;
    private readonly AuditLog _audit;
    private readonly GlobalContext? _global;
    private readonly Action<string>? _log;

    public ExecutionEngine(
        IGitService git, TenNinetyConfig config, IFrontierClient frontier,
        ICoderAgent coder, IReviewerAgent reviewer, ITesterAgent tester,
        StateStore stateStore, AuditLog audit, GlobalContext? globalContext = null,
        Action<string>? log = null)
    {
        _global = globalContext;
        _git = git;
        _config = config;
        _frontier = frontier;
        _coder = coder;
        _reviewer = reviewer;
        _tester = tester;
        _stateStore = stateStore;
        _audit = audit;
        _log = log;
    }

    public async Task<WpOutcome> ExecuteWpAsync(WorkPackage wp, RuntimeState state, CancellationToken ct)
    {
        var branch = TenNinety.WorkBranchPrefix + wp.Id;
        // Resume/rework/crash-recovery: an existing branch is REUSED so attempts build on
        // prior commits. Creation happens only when absent. The base-branch safety check
        // applies to BOTH paths (external review Major 2 regression fix).
        if (_git.CurrentBranch() != TenNinety.MainBranch)
            throw new InvalidOperationException(
                $"the framework must start from branch '{TenNinety.MainBranch}' " +
                $"but found '{_git.CurrentBranch()}'. Switch to the base branch first – " +
                "otherwise a promotion could drag an unrelated feature branch onto main.");

        var expectedMainSha = _git.FindCommit(TenNinety.MainBranch)?.Sha
            ?? throw new InvalidOperationException("main has no commit to use as a work-package base.");

        var resumingBranch = _git.BranchExists(branch);
        if (resumingBranch)
            _git.CheckoutBranch(branch);
        else
            _git.CreateAndCheckoutBranch(branch);

        try
        {
            // Defensive checkpoint: a crashed previous attempt may have left uncommitted files.
            if (!_git.IsClean() && (_config.Sandbox.IsUnsafeHost || _config.NormalizedProviderMode == "mock"))
            {
                _git.CommitAll($"{wp.Id}: wip checkpoint");
                _log?.Invoke($"[{wp.Id}] committed leftover working-tree changes as a WIP checkpoint");
            }
            else if (!_git.IsClean())
            {
                throw new InvalidOperationException(
                    "the Docker execution path requires a clean authoritative repository; " +
                    "automatic CommitAll checkpointing is forbidden.");
            }
            if (resumingBranch)
            {
                _git.MergeMainIntoCurrentBranch();
                EnsureBranchAndBaseUnchanged(branch, expectedMainSha, "resume merge", requireClean: true);
            }

            var info = GetAttemptInfo(state, wp.Id);
            info.Max = _config.MaxAttemptsBeforeEscalation;
            state.CurrentWp = wp.Id;
            wp.Status = TenNinety.WpStatus.Active;
            SyncQueue(state, wp.Id, TenNinety.WpStatus.Active);
            Persist(state);
            _audit.Append("WP_STARTED", wp.Id, $"branch={branch}");
            _log?.Invoke($"[{wp.Id}] started on branch '{branch}'");

            while (true)
            {
                if (PollControl(state, wp) is { } interruptAtCycleTop)
                    return interruptAtCycleTop;
                if (state.Paused || state.StopRequested || ct.IsCancellationRequested)
                    return HandleInterruption(wp, state);

                checked
                {
                    info.Count++;
                    info.Total++;
                }
                Persist(state);
                _log?.Invoke($"[{wp.Id}] attempt {info.Total} (phase count {info.Count}/{info.Max})");

                // 1. CODER
                var coderBase = new CandidateRevision(branch, _git.HeadSha(), expectedMainSha);
                var coderCtx = MakeCoderContext(wp, info, coderBase);
                CoderResult code;
                try
                {
                    code = await _coder.ImplementAsync(coderCtx, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (_config.Sandbox.IsUnsafeHost || _config.NormalizedProviderMode == "mock")
                        CheckpointWork(wp.Id, branch, "interrupted");
                    state.StopRequested = true;
                    return HandleInterruption(wp, state);
                }
                catch (CoderInfrastructureException ex)
                {
                    ReleaseInfrastructureAttempt(info);
                    Persist(state);
                    _audit.Append("CODER_FAILED", wp.Id,
                        $"infrastructure exception: {Truncate(Sanitise(ex.Message), 200)}");
                    throw;
                }
                catch (Exception ex)
                {
                    RecordFailure(info, TenNinety.FailureTypes.Coder,
                        Sanitise($"coder exception: {ex.Message}"));
                    _audit.Append("CODER_FAILED", wp.Id, Truncate(Sanitise(ex.Message), 200));
                    _log?.Invoke($"[{wp.Id}] coder exception: {ex.Message}");
                    if (await HandleThresholdAsync(wp, state, info, ct)) return WpOutcome.Blocked;
                    continue;
                }

                EnsureBranchAndBaseUnchanged(branch, expectedMainSha, "coder");
                var sha = code.CommitSha;
                if (sha is null &&
                    (_config.Sandbox.IsUnsafeHost || _config.NormalizedProviderMode == "mock"))
                    sha = _git.CommitAll(
                        $"{wp.Id}: {Truncate(Sanitise(code.Summary), 80)} [attempt {info.Total}]");
                if (sha is null || !code.ProducesRealChange)
                {
                    RecordFailure(info, TenNinety.FailureTypes.Coder,
                        "no file changes were produced by the coder.");
                    _audit.Append("CODER_NO_CHANGE", wp.Id);
                    if (await HandleThresholdAsync(wp, state, info, ct)) return WpOutcome.Blocked;
                    continue;
                }
                if (!string.Equals(_git.HeadSha(), sha, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "the coder result commit does not equal the authoritative work-branch HEAD.");
                EnsureBranchAndBaseUnchanged(branch, expectedMainSha, "coder commit", requireClean: true);
                _audit.Append("CODER_COMMITTED", wp.Id, $"{sha[..Math.Min(12, sha.Length)]} {code.FilesTouched.Count} files");

                if (PollControl(state, wp) is { } afterCoder) return afterCoder;

                // 2. REVIEWER - receives only the exact committed candidate plus instructions.
                // Docker review explores a fresh offline materialization through bounded guest
                // actions; explicit unsafe-host may derive a bounded diff through its trusted
                // constructor-injected Git dependency.
                ReviewResult review;
                try
                {
                    review = await _reviewer.ReviewAsync(
                        MakeReviewerContext(wp, info,
                            new CandidateRevision(branch, sha, expectedMainSha)), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (_config.Sandbox.IsUnsafeHost || _config.NormalizedProviderMode == "mock")
                        CheckpointWork(wp.Id, branch, "interrupted");
                    state.StopRequested = true;
                    return HandleInterruption(wp, state);
                }
                catch (Exception ex)
                {
                    ReleaseInfrastructureAttempt(info);
                    Persist(state);
                    _audit.Append("REVIEW_FAILED", wp.Id,
                        $"infrastructure exception: {Truncate(Sanitise(ex.Message), 200)}");
                    throw;
                }
                EnsureBranchAndBaseUnchanged(branch, expectedMainSha, "reviewer", requireClean: true);
                if (!string.Equals(review.CandidateSha, sha, StringComparison.Ordinal))
                {
                    RecordFeedback(info, TenNinety.FailureTypes.Reviewer,
                        "the reviewer did not return the exact requested candidate identity.");
                    _audit.Append("REVIEW_FAILED", wp.Id, "candidate identity missing or mismatched");
                    if (await HandleThresholdAsync(wp, state, info, ct)) return WpOutcome.Blocked;
                    continue;
                }
                if (!review.Passed)
                {
                    foreach (var reason in review.Reasons.Take(5))
                        RecordFeedback(info, TenNinety.FailureTypes.Reviewer, Sanitise(reason));
                    _audit.Append("REVIEW_FAILED", wp.Id,
                        Truncate(Sanitise(string.Join(" | ", review.Reasons.Take(3))), 500));
                    _log?.Invoke($"[{wp.Id}] review FAILED ({review.Reasons.Count} reasons)");
                    if (await HandleThresholdAsync(wp, state, info, ct)) return WpOutcome.Blocked;
                    continue;
                }
                _audit.Append("REVIEW_PASSED", wp.Id, review.ReviewerModel);
                var reviewedTip = sha;

                if (PollControl(state, wp) is { } afterReview) return afterReview;

                // 3. TESTER (mechanical) – receives ONLY the trusted candidate identity
                // (the reviewed tip), never the authoritative repo path: the Tester context
                // is TesterRunContext, not WpContext.
                TestRunResult test;
                try
                {
                    test = await _tester.RunTestsAsync(new TesterRunContext
                    {
                        Candidate = new CandidateRevision(branch, reviewedTip, expectedMainSha),
                        WorkPackageId = wp.Id,
                        Attempt = Math.Max(1, info.Count),
                        Advice = info.Advice,
                    }, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    state.StopRequested = true;
                    return HandleInterruption(wp, state);
                }
                catch (Exception ex)
                {
                    ReleaseInfrastructureAttempt(info);
                    Persist(state);
                    _audit.Append("TESTS_FAILED", wp.Id,
                        $"infrastructure exception: {Truncate(Sanitise(ex.Message), 200)}");
                    throw;
                }
                EnsureBranchAndBaseUnchanged(branch, expectedMainSha, "tester");
                if (!test.Passed)
                {
                    RecordFeedback(info, TenNinety.FailureTypes.Tester,
                        Sanitise($"tests exited {test.ExitCode}. Output tail:\n{test.OutputTail}"));
                    _audit.Append("TESTS_FAILED", wp.Id, $"exit={test.ExitCode}");
                    _log?.Invoke($"[{wp.Id}] tests FAILED (exit {test.ExitCode})");
                    if (await HandleThresholdAsync(wp, state, info, ct)) return WpOutcome.Blocked;
                    continue;
                }

                // Exact candidate identity enforcement: a missing or mismatched result SHA is
                // never accepted as a passing gate, and it is never "repaired" from the
                // current HEAD. Identity comes from trusted orchestration only.
                if (!string.Equals(test.CandidateSha, reviewedTip, StringComparison.Ordinal))
                {
                    RecordFeedback(info, TenNinety.FailureTypes.Tester,
                        "the tester did not return the exact requested candidate identity; refusing the gate.");
                    _audit.Append("TESTS_FAILED", wp.Id, "candidate identity missing or mismatched");
                    _log?.Invoke($"[{wp.Id}] tests rejected: candidate identity missing or mismatched");
                    if (await HandleThresholdAsync(wp, state, info, ct)) return WpOutcome.Blocked;
                    continue;
                }

                if (PollControl(state, wp) is { } afterTester) return afterTester;
                EnsureBranchAndBaseUnchanged(branch, expectedMainSha, "tester", requireClean: true);
                if (_git.HeadSha() != reviewedTip)
                    throw new InvalidOperationException(
                        "test command changed the reviewed work-branch commit; refusing unreviewed promotion.");

                // 4. PROMOTE – always as ONE squashed commit so reverting a package is exact.
                var branchTip = _git.HeadSha();
                var mergeSha = _git.SquashMergeToMain(
                    branch, $"{wp.Id}: {wp.Title} [work package]");
                TryDeleteBranch(branch);
                wp.Status = TenNinety.WpStatus.Done;
                state.CurrentWp = null;
                state.Attempts.Remove(wp.Id);
                SyncQueue(state, wp.Id, TenNinety.WpStatus.Done);
                Persist(state);
                _audit.Append("WP_PROMOTED", wp.Id,
                    $"merge={mergeSha[..Math.Min(12, mergeSha.Length)]} " +
                    $"branchTip={branchTip[..Math.Min(12, branchTip.Length)]}");
                _log?.Invoke($"[{wp.Id}] PASSED — promoted to main");
                return WpOutcome.Done;
            }
        }
        catch
        {
            if (_git.CurrentBranch() == branch && !_git.IsClean() &&
                (_config.Sandbox.IsUnsafeHost || _config.NormalizedProviderMode == "mock"))
                CheckpointWork(wp.Id, branch, "fault");
            // Infrastructure/process faults abort the run by owner decision, but the persisted
            // package must remain resumable rather than becoming a stale ACTIVE deadlock.
            if (wp.Status == TenNinety.WpStatus.Active)
            {
                wp.Status = TenNinety.WpStatus.Pending;
                state.CurrentWp = null;
                SyncQueue(state, wp.Id, TenNinety.WpStatus.Pending);
                Persist(state);
            }
            throw;
        }
        finally
        {
            // Return to main only after all work is safely committed. If checkpointing failed,
            // leave the dirty work branch in place rather than carrying partial edits to main.
            if (_git.CurrentBranch() == branch && _git.IsClean())
                _git.CheckoutBranch(TenNinety.MainBranch);
        }
    }

    private WpOutcome HandleInterruption(WorkPackage wp, RuntimeState state)
    {
        if (state.StopRequested)
        {
            wp.Status = TenNinety.WpStatus.Pending;
            state.CurrentWp = null;
            SyncQueue(state, wp.Id, TenNinety.WpStatus.Pending);
            Persist(state);
            _audit.Append("DAEMON_STOPPED", wp.Id);
            return WpOutcome.Stopped;
        }

        wp.Status = TenNinety.WpStatus.Pending;
        state.CurrentWp = null;
        SyncQueue(state, wp.Id, TenNinety.WpStatus.Pending);
        Persist(state);
        _audit.Append("PAUSED", wp.Id, $"attempt {GetAttemptInfo(state, wp.Id).Total}");
        _log?.Invoke($"[{wp.Id}] paused — state saved");
        return WpOutcome.Paused;
    }

    /// <summary>
    /// Applies the 10/20 thresholds after a failed round. Returns a non-null outcome when the
    /// loop must stop (BLOCKED); null means "retry". Escalation awaits the Frontier with the
    /// caller's token so pause/stop can interrupt an in-flight advice call.
    /// </summary>
    private async Task<bool> HandleThresholdAsync(
        WorkPackage wp, RuntimeState state, AttemptInfo info, CancellationToken ct)
    {
        Persist(state);

        if (info.Total >= _config.MaxTotalAttempts)
        {
            wp.Status = TenNinety.WpStatus.Blocked;
            state.CurrentWp = null;
            SyncQueue(state, wp.Id, TenNinety.WpStatus.Blocked);
            Persist(state);
            _audit.Append("WP_BLOCKED", wp.Id, $"{info.Total} failed attempts — human action required");
            _log?.Invoke($"[{wp.Id}] BLOCKED after {info.Total} attempts");
            return true;
        }

        if (info.Count >= info.Max)
        {
            _log?.Invoke($"[{wp.Id}] escalating to Frontier for repair advice…");
            var request = new RepairRequest(
                wp, info.Total, info.Feedback,
                info.Advice.LastOrDefault(),
                string.Join("\n", _audit.ReadTail(10).Select(e => $"{e.Timestamp} {e.Event} {e.Detail}")),
                SafeDiff(wp.Id));
            RepairAdvice advice;
            try
            {
                advice = await _frontier.GetRepairAdviceAsync(request, ct);
            }
            catch (Exception ex)
            {
                Persist(state);
                _audit.Append("ADVICE_UNAVAILABLE", wp.Id, Truncate(Sanitise(ex.Message), 200));
                throw;
            }

            info.Count = 0;
            info.FrontierAdviceUsed = true;
            info.Advice.Add(advice.Analysis);
            info.Advice.AddRange(advice.Advice);
            // Advice resets the local budget; cap stored context so prompts stay bounded.
            if (info.Feedback.Count > 20) info.Feedback = info.Feedback.TakeLast(20).ToList();
            Persist(state);
            _audit.Append("ESCALATION_ADVICE", wp.Id, Truncate(advice.Analysis, 200));
            _log?.Invoke($"[{wp.Id}] advice injected — local counter reset");
        }

        return false;
    }

    private static void ReleaseInfrastructureAttempt(AttemptInfo info)
    {
        info.Count = Math.Max(0, info.Count - 1);
        info.Total = Math.Max(0, info.Total - 1);
    }

    /// <summary>
    /// Consumes cross-process control requests at a safe point. Returns non-null when the
    /// loop must stop: the job is reset to PENDING exactly like HandleInterruption, so
    /// resume continues on the same branch with budgets intact.
    /// </summary>
    private WpOutcome? PollControl(RuntimeState state, WorkPackage wp)
    {
        var (pauseRequested, stopRequested) = ExecutionControl.ConsumeFlags(_git.RepoPath);
        if (stopRequested)
        {
            state.StopRequested = true;
            return HandleInterruption(wp, state);
        }
        if (pauseRequested || state.Paused)
        {
            state.Paused = true;
            return HandleInterruption(wp, state);
        }
        return null;
    }

    private CoderRunContext MakeCoderContext(
        WorkPackage wp, AttemptInfo info, CandidateRevision candidate) => new()
    {
        Candidate = candidate,
        WorkPackage = wp,
        Global = _global,
        Attempt = Math.Max(1, info.Count),
        Feedback = info.Feedback,
        Advice = info.Advice,
    };

    private ReviewerRunContext MakeReviewerContext(
        WorkPackage wp, AttemptInfo info, CandidateRevision candidate) => new()
    {
        Candidate = candidate,
        WorkPackage = wp,
        Global = _global,
        Attempt = Math.Max(1, info.Count),
        Feedback = info.Feedback,
        Advice = info.Advice,
    };

    private static AttemptInfo GetAttemptInfo(RuntimeState state, string wpId)
    {
        if (!state.Attempts.TryGetValue(wpId, out var info))
        {
            info = new AttemptInfo();
            state.Attempts[wpId] = info;
        }
        return info;
    }

    private static void RecordFailure(AttemptInfo info, string type, string reason)
    {
        info.LastFailureType = type;
        info.LastFailureReasons = new List<string> { reason };
        RecordFeedback(info, type, reason);
    }

    private static void RecordFeedback(AttemptInfo info, string type, string reason) =>
        info.Feedback.Add($"[{type}] {reason}");

    private void Persist(RuntimeState state) => _stateStore.Save(state);

    private void SyncQueue(RuntimeState state, string wpId, string status) => state.QueueStatus[wpId] = status;

    private void TryDeleteBranch(string branch)
    {
        try { _git.DeleteBranchSafe(branch, force: true); } // content is squash-merged onto main
        catch { /* non-fatal */ }
    }

    private void CheckpointWork(string wpId, string branch, string reason)
    {
        if (_git.CurrentBranch() != branch)
            throw new InvalidOperationException(
                $"interrupted coder left the workspace on an unexpected branch; expected '{branch}'.");
        if (_git.IsClean()) return;
        _git.CommitAll($"{wpId}: {reason} checkpoint");
        if (!_git.IsClean())
            throw new InvalidOperationException(
                $"could not checkpoint all interrupted changes for '{wpId}'; work branch retained.");
        _log?.Invoke($"[{wpId}] checkpointed partial work after {reason}");
    }

    private string SafeDiff(string wpId)
    {
        try { return Sanitise(_git.DiffPatchAgainstMain(TenNinety.WorkBranchPrefix + wpId)); }
        catch { return ""; }
    }

    private static string Sanitise(string s) => Core.Security.Sanitizer.SanitizeText(s ?? "");

    private void EnsureBranchAndBaseUnchanged(
        string branch, string expectedMainSha, string stage, bool requireClean = false)
    {
        var current = _git.CurrentBranch();
        if (current != branch)
            throw new InvalidOperationException(
                $"{stage} changed branches from '{branch}' to '{current}'; refusing to continue.");
        var mainSha = _git.FindCommit(TenNinety.MainBranch)?.Sha;
        if (mainSha != expectedMainSha)
            throw new InvalidOperationException(
                $"{stage} changed the main branch while a work package was active; refusing to continue.");
        if (requireClean && !_git.IsClean())
            throw new InvalidOperationException(
                $"{stage} modified files after the reviewed commit; refusing unreviewed promotion.");
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>C# 14 extension members: an extension property on <see cref="CoderResult"/>.</summary>
internal static class CoderResultExtensions
{
    extension(CoderResult result)
    {
        public bool ProducesRealChange => result.ProducedChanges || result.FilesTouched.Count > 0;
    }
}
