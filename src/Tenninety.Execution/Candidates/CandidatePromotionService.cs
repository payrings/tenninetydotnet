using Tenninety.Core;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Execution.Candidates;

/// <summary>Preconditions and trusted message for one validated promotion. These are bound to
/// the workspace and validated patch at apply time: work branch, base commit and main base
/// must agree with the workspace revision and the patch — any mismatch rejects before
/// application.</summary>
public sealed record PromotionPreconditions(
    string WorkBranch,
    string BaseCommitSha,
    string MainBaseSha,
    string CommitMessage);

/// <summary>The outcome of a validated promotion attempt.</summary>
public sealed record CandidatePromotionResult(
    bool NoChanges,
    string? CommitSha,
    string? TargetTreeOid,
    CandidatePatch? Patch,
    int ChangedFileCount);

/// <summary>Internal deterministic fault-injection points for rollback tests. The production
/// default is <see cref="CandidatePromotionFaultPoint.None"/>; the seam is internal and can
/// never be influenced by workspace content or configuration.</summary>
internal enum CandidatePromotionFaultPoint
{
    None,
    /// <summary>Throw immediately after the mutating apply command returned.</summary>
    AfterApplyMutated,
    /// <summary>Throw after the applied index was verified, before the commit.</summary>
    BeforeCommit,
    /// <summary>Throw after the commit object was created and verified, before the
    /// work-branch ref advanced.</summary>
    AfterCommit,
    /// <summary>Throw after the work-branch ref was advanced by compare-and-swap to the
    /// operation commit, before the final verification and normal return.</summary>
    AfterRefAdvance,
}

/// <summary>Options for one validated promotion run; all fail closed.</summary>
public sealed record CandidatePromotionOptions
{
    public CandidateScanLimits Scan { get; init; } = new();

    public PromotionPolicyOptions Policy { get; init; } = new();

    /// <summary>Maximum candidate patch size in bytes.</summary>
    public long MaxPatchBytes { get; init; } = 64L * 1024 * 1024;

    public void Validate()
    {
        if (Scan is null) throw new InvalidOperationException("scan options must not be null.");
        if (Policy is null) throw new InvalidOperationException("policy options must not be null.");
        Scan.Validate();
        Policy.Validate();
        if (MaxPatchBytes is < 1 or > 1_073_741_824)
            throw new InvalidOperationException(
                $"promotion MaxPatchBytes must be within [1, 1073741824] but is {MaxPatchBytes}.");
    }
}

/// <summary>
/// Orchestrates the trusted promotion of one coder workspace:
///
///  1. options are validated FIRST — including on the no-change route;
///  2. only CODER workspaces may ever be promoted (reviewer/tester are always discarded);
///  3. a LIVE daemon-lock lease for the SAME repository is mandatory and is verified before
///     anything runs (the engine threads its existing lease through — never a nested acquire);
///  4. the stop-before-scan rule is enforced (identity-bound confirmed quiescence proof);
///  5. the scan builds an exact target index from an empty index state and derives the
///     cross-checked NUL-delimited manifest; tree equality with the baseline detects the
///     no-change outcome without building any patch;
///  6. the promotion policy evaluates the whole change set (accept-all or reject-all);
///  7. the opaque validated patch is built (size-capped, SHA-256 hashed, audit copy written
///     once) and bound to the workspace identity;
///  8. immediately before application, while the lease is confirmed live, the preconditions
///     are asserted and all bindings are cross-checked (workspace revision == patch base
///     commit, baseline tree == patch base tree, branch/main base agreement, patch hash);
///  9. the patch is CHECK-applied, then applied atomically to index and worktree from the
///     exact hashed bytes via standard input; `git write-tree` must equal the trusted target
///     tree OID; the commit is created with TENNINETY's pinned author/committer identity and
///     its tree is verified to equal the target tree OID;
/// 10. a mutation guard is set immediately BEFORE the apply command: on any failure at or
///     after that point the rollback (a) compare-and-swaps the work-branch ref from the
///     operation-created commit back to the recorded pre-apply HEAD when applicable and
///     (b) restores ONLY the validated change paths from the recorded pre-apply commit, then
///     verifies exact recovery (branch, HEAD, tree, clean index/worktree) — surfacing a loud
///     composite failure if exact recovery cannot be proven.
///
/// `CommitAll` is never used for sandbox promotion.
/// </summary>
public sealed class CandidatePromotionService
{
    private readonly IGitService _git;

    internal CandidatePromotionFaultPoint FaultInjection { get; set; }
        = CandidatePromotionFaultPoint.None;

    public CandidatePromotionService(IGitService authoritativeGit) => _git = authoritativeGit;

    /// <summary>Instance-scoped internal pause hook used ONLY by deterministic lock-lifetime
    /// tests: invoked once per promotion immediately after the operation guard is acquired.
    /// The production default is null; it is never influenced by workspace content.</summary>
    internal Action? OperationPauseHook { get; set; }

    public CandidatePromotionResult PromoteValidated(
        CandidateWorkspace workspace,
        QuiescenceProof proof,
        CandidatePromotionOptions options,
        PromotionPreconditions preconditions,
        DaemonLockLease lease,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        // Options are validated before anything else — including on the no-change route.
        options.Validate();
        RequireLiveLease(lease);
        RequireCoderWorkspace(workspace);

        // ONE operation guard is held continuously from before scanning through the
        // no-change return, successful commit verification, or completed rollback: the OS
        // lock cannot be released by an outer lease disposal while promotion runs.
        using var operation = lease.BeginUseFor(_git.RepoPath);
        operation.EnsureLiveFor(_git.RepoPath);
        OperationPauseHook?.Invoke();

        var scan = new CandidateScanner(_git).Scan(workspace, proof, options.Scan, ct);

        // No-change detection from tree equality: no patch, no application, nothing mutated.
        if (string.Equals(scan.TargetTreeOid, workspace.BaselineTreeOid, StringComparison.Ordinal))
            return new CandidatePromotionResult(
                NoChanges: true, CommitSha: null, TargetTreeOid: null,
                Patch: null, ChangedFileCount: 0);

        PromotionPolicy.Evaluate(scan.Changes, options.Policy, scan.TargetEntries);

        var validatedPatch = new CandidatePatchBuilder().Build(
            workspace, scan, options.MaxPatchBytes, ct);

        var commitSha = ApplyValidated(workspace, validatedPatch, preconditions, operation);
        return new CandidatePromotionResult(
            NoChanges: false, CommitSha: commitSha,
            TargetTreeOid: validatedPatch.TargetTreeOid,
            Patch: validatedPatch.ToInertSnapshot(),
            ChangedFileCount: scan.Changes.Count);
    }

    /// <summary>Applies the opaque validated patch under the asserted preconditions while a
    /// live operation guard keeps the daemon lock held. Internal: only trusted orchestration
    /// can apply; ordinary callers cannot fabricate or mutate the patch capability, and the
    /// guard makes it impossible to run without the lock.</summary>
    internal string ApplyValidated(
        CandidateWorkspace workspace,
        ValidatedCandidatePatch patch,
        PromotionPreconditions preconditions,
        Execution.DaemonLockLease.DaemonLockOperationGuard operation)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(patch);
        ArgumentNullException.ThrowIfNull(preconditions);
        ArgumentNullException.ThrowIfNull(operation);
        // The guard is verified LIVE and bound to THIS repository right now (no
        // check-then-use gap: the same guard is held for the whole apply, so the OS lock
        // cannot be released under us even if the outer lease is disposed concurrently).
        // A guard issued for another repository rejects here before the audit patch is
        // read or any authoritative Git state is touched.
        operation.EnsureLiveFor(_git.RepoPath);
        RequireCoderWorkspace(workspace);

        // Cross-check every binding: preconditions, workspace and patch must name the same
        // base commit, base tree, branch and main base; the patch must belong to this
        // workspace; and its byte hash must match the recorded digest.
        if (!string.Equals(patch.BaseCommitSha, workspace.Revision.CommitSha, StringComparison.Ordinal) ||
            !string.Equals(patch.BaseTreeOid, workspace.BaselineTreeOid, StringComparison.Ordinal) ||
            !string.Equals(patch.WorkspaceAttemptRoot, workspace.AttemptRoot, StringComparison.Ordinal) ||
            patch.WorkspaceRole != workspace.Role ||
            !string.Equals(patch.WorkspaceRunId, workspace.RunId, StringComparison.Ordinal) ||
            !string.Equals(patch.WorkspaceAttemptId, workspace.AttemptId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the validated patch is not bound to this workspace; refusing to apply.");
        if (!string.Equals(preconditions.BaseCommitSha, workspace.Revision.CommitSha, StringComparison.Ordinal) ||
            !string.Equals(preconditions.WorkBranch, workspace.Revision.WorkBranch, StringComparison.Ordinal) ||
            !string.Equals(preconditions.MainBaseSha, workspace.Revision.MainBaseSha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the promotion preconditions do not describe this workspace and patch; " +
                "refusing to apply.");
        // The persisted audit copy is loaded ONCE through the no-follow regular-file reader
        // (descriptor metadata proves type/size and detects replacement) and must hash to the
        // validated digest AND equal the frozen bytes — a tampered or replaced audit file
        // rejects the promotion before any host Git command.
        var auditBytes = ReadAuditCopy(patch);
        if (!string.Equals(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(auditBytes))
                    .ToLowerInvariant(),
                patch.PatchSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the validated patch audit copy failed its SHA-256 integrity check; " +
                "refusing to apply.");
        if (!auditBytes.AsSpan().SequenceEqual(patch.GetFrozenPatchBytesSnapshot()))
            throw new InvalidOperationException(
                "the validated patch audit copy does not match the validated patch bytes; " +
                "refusing to apply.");

        // Pre-apply records for exact recovery.
        var preApplyBranch = _git.CurrentBranch();
        var preApplyHead = _git.HeadSha();
        var preApplyTree = _git.ResolveTreeOfCommit(preApplyHead);
        AssertPreconditions(preconditions);
        // Cross-check the pre-apply HEAD against the patch base one final time.
        if (!string.Equals(preApplyHead, patch.BaseCommitSha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the pre-apply HEAD does not equal the patch base commit; refusing to apply.");

        // The mutation guard is set immediately BEFORE the mutating command, not after it
        // returns: a failure thrown by or during the apply must still trigger recovery.
        var mutationMayHaveStarted = false;
        string? operationCommit = null;
        try
        {
            var frozenPatchBytes = patch.GetFrozenPatchBytesSnapshot();
            _git.VerifyPatchBytesApplyToIndexAndWorktree(frozenPatchBytes);
            mutationMayHaveStarted = true;
            _git.ApplyPatchBytesToIndexAndWorktree(frozenPatchBytes);
            ThrowIfFaultInjected(CandidatePromotionFaultPoint.AfterApplyMutated);

            var writtenTree = _git.WriteTree();
            if (!string.Equals(writtenTree, patch.TargetTreeOid, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the authoritative index tree does not equal the trusted target tree " +
                    "OID; promotion aborted.");
            ThrowIfFaultInjected(CandidatePromotionFaultPoint.BeforeCommit);

            // Create the COMMIT OBJECT first: the service must know the operation commit OID
            // (and its tree/parent) BEFORE the work-branch ref can advance. No ref moves
            // during commit-tree.
            operationCommit = _git.CreateCommitObjectForTree(
                patch.TargetTreeOid, preApplyHead, preconditions.CommitMessage);
            var committedTree = _git.ResolveTreeOfCommit(operationCommit);
            if (!string.Equals(committedTree, patch.TargetTreeOid, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the promotion commit's tree does not equal the trusted target tree OID; " +
                    "promotion aborted.");
            var parentSha = _git.ResolveCommitParent(operationCommit);
            if (!string.Equals(parentSha, preApplyHead, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the promotion commit's parent does not equal the pre-apply HEAD; " +
                    "promotion aborted.");
            ThrowIfFaultInjected(CandidatePromotionFaultPoint.AfterCommit);

            // Advance ONLY refs/heads/<validated work branch> from the pre-apply commit to
            // the known operation commit via compare-and-swap.
            _git.UpdateRefCompareAndSwap(
                $"refs/heads/{preconditions.WorkBranch}", operationCommit, preApplyHead);
            ThrowIfFaultInjected(CandidatePromotionFaultPoint.AfterRefAdvance);

            // The worktree/index already match the target, so they must be clean against
            // the new HEAD.
            if (!string.Equals(_git.HeadSha(), operationCommit, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the work branch did not advance to the operation commit; promotion " +
                    "aborted.");
            if (!_git.IsClean())
                throw new InvalidOperationException(
                    "the authoritative worktree and index are not clean against the new " +
                    "HEAD; promotion aborted.");
            return operationCommit;
        }
        catch (Exception failure)
        {
            if (mutationMayHaveStarted)
                RecoverToPreApplyState(preconditions, patch, preApplyBranch, preApplyHead,
                    preApplyTree, operationCommit, failure);
            throw;
        }
    }

    /// <summary>Exact recovery to the recorded pre-apply state using only the validated
    /// change paths and a compare-and-swap ref update — never reset/clean/checkout-all. The
    /// operation commit OID is known exactly (created via commit-tree before any ref moved),
    /// so recovery can CAS only that ref from the operation commit back to the pre-apply
    /// commit. Both the original failure and any rollback failure are surfaced together.</summary>
    private void RecoverToPreApplyState(
        PromotionPreconditions preconditions,
        ValidatedCandidatePatch patch,
        string preApplyBranch,
        string preApplyHead,
        string preApplyTree,
        string? operationCommit,
        Exception failure)
    {
        try
        {
            // (a) If this operation created a commit and the work branch still points exactly
            // at it, compare-and-swap only that ref back to the pre-apply commit.
            if (operationCommit is not null)
            {
                var currentHead = _git.HeadSha();
                if (string.Equals(currentHead, operationCommit, StringComparison.Ordinal))
                    _git.UpdateRefCompareAndSwap(
                        $"refs/heads/{preconditions.WorkBranch}", preApplyHead, operationCommit);
                else if (!string.Equals(currentHead, preApplyHead, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "the work branch moved to an unexpected commit during recovery.");
            }

            // (b) Restore only the validated change paths from the recorded pre-apply commit.
            foreach (var change in patch.Changes)
            {
                if (change.Kind == GitChangeKind.Added)
                {
                    // The path did not exist at the pre-apply commit: remove it from the
                    // index and the worktree.
                    try
                    {
                        _git.RemoveFromIndex(change.NormalizedPath);
                        var worktreePath = Path.Combine(_git.RepoPath, change.NormalizedPath);
                        if (File.Exists(worktreePath)) File.Delete(worktreePath);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"rollback could not remove the validated added path " +
                            $"'{change.NormalizedPath}'.", ex);
                    }
                }
                else
                {
                    _git.RestorePathFromCommit(preApplyHead, change.NormalizedPath);
                }
            }

            // (c) Prove exact recovery: branch, HEAD, tree, clean index and worktree.
            if (!string.Equals(_git.CurrentBranch(), preApplyBranch, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "rollback could not restore the original branch.");
            if (!string.Equals(_git.HeadSha(), preApplyHead, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "rollback could not restore the original HEAD commit.");
            if (!string.Equals(_git.WriteTree(), preApplyTree, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "rollback could not restore the original tree.");
            if (!_git.IsClean())
                throw new InvalidOperationException(
                    "rollback could not restore a clean index and worktree.");
        }
        catch (Exception rollbackFailure)
        {
            // BOTH failures travel together: the original promotion failure and the
            // rollback failure, inside the loud repository-unsafe exception.
            throw new InvalidOperationException(
                "the promotion failed and exact recovery could not be proven; the " +
                "repository is marked unsafe for retry and must be inspected.",
                new AggregateException(failure, rollbackFailure));
        }
    }

    /// <summary>Reads the audit copy of the patch ONCE through the no-follow regular-file
    /// reader (descriptor metadata proves type/size and detects replacement). The caller
    /// verifies the SHA-256 of the returned bytes against the validated digest.</summary>
    private byte[] ReadAuditCopy(ValidatedCandidatePatch patch)
    {
        using var opened = TrustedFileReader.OpenRegularFileNoFollow(patch.AuditFilePath);
        if (opened.Length != patch.PatchByteLength)
            throw new InvalidOperationException(
                "the validated patch audit copy has an unexpected size; refusing to apply.");
        using var stream = new FileStream(
            opened.Handle, FileAccess.Read, bufferSize: 81920, isAsync: false);
        var bytes = new byte[opened.Length];
        var read = 0;
        while (read < bytes.Length)
        {
            var n = stream.Read(bytes, read, bytes.Length - read);
            if (n == 0) break;
            read += n;
        }
        opened.VerifyUnchanged(read);
        if (read != bytes.Length)
            throw new InvalidOperationException(
                "the validated patch audit copy could not be read completely; refusing to apply.");
        return bytes;
    }

    private void RequireLiveLease(DaemonLockLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lease.ThrowIfNotLiveFor(_git.RepoPath);
    }

    private void ThrowIfFaultInjected(CandidatePromotionFaultPoint point)
    {
        if (FaultInjection == point)
            throw new InvalidOperationException(
                $"deterministic fault injected at {point} (test seam).");
    }

    private void AssertPreconditions(PromotionPreconditions preconditions)
    {
        var branch = _git.CurrentBranch();
        if (!string.Equals(branch, preconditions.WorkBranch, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"the expected work branch '{preconditions.WorkBranch}' is not checked out " +
                $"(found '{branch}'); refusing to apply the candidate patch.");
        var head = _git.HeadSha();
        if (!string.Equals(head, preconditions.BaseCommitSha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the work-branch HEAD does not equal the patch base candidate; refusing to " +
                "apply the candidate patch.");
        var mainSha = _git.FindCommit(TenNinety.MainBranch)?.Sha;
        if (!string.Equals(mainSha, preconditions.MainBaseSha, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "main is not at the recorded base SHA; refusing to apply the candidate patch.");
        if (!_git.IsClean())
            throw new InvalidOperationException(
                "the authoritative worktree and index must be clean before promotion; " +
                "refusing to apply the candidate patch.");
    }

    private static void RequireCoderWorkspace(CandidateWorkspace workspace)
    {
        if (workspace.Role != SandboxRole.Coder)
            throw new InvalidOperationException(
                "only coder workspaces may be promoted: reviewer and tester filesystems are " +
                "always discarded and their paths are never accepted by this API.");
    }
}
