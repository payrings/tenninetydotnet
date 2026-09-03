using System.Text;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Phase 3 repair tests, second round: (a) Linux no-follow semantics including symlink kinds
/// that O_PATH|O_NOFOLLOW opens as themselves, deterministic mode/content change detection;
/// (b) approved baseline content under transient roots preserved exactly; (c) daemon-lock
/// operation-guard lifetime; (d) commit-object/CAS ref advancement with deterministic fault
/// injection and composite rollback failures.
/// </summary>
public class Phase3RepairTests : IDisposable
{
    private readonly TestGitRepo _repo = new();
    private readonly TempDir _managedRoot = new();

    public void Dispose()
    {
        _managedRoot.Dispose();
        _repo.Dispose();
    }

    private CandidateWorkspace Workspace { get; }
    private string MainSha { get; }
    private string CandidateSha { get; }
    private CandidatePromotionService Service { get; }
    private DaemonLockLease Lease { get; }
    private QuiescenceProof Proof { get; }
    private PromotionPreconditions Preconditions { get; }

    public Phase3RepairTests()
    {
        _repo.WriteFile("sentinel.txt", "sentinel\n");
        _repo.Commit("initial on main");
        MainSha = _repo.Git.HeadSha();
        _repo.Git.CreateAndCheckoutBranch("work/WP-001");
        _repo.WriteFile("src/existing.txt", "original\n");
        CandidateSha = _repo.Commit("candidate");
        Workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = CandidateSha,
                ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001",
                MainBaseSha = MainSha,
                Role = SandboxRole.Coder,
                RunId = "run-1",
                AttemptId = "attempt-1",
            });
        Service = new CandidatePromotionService(_repo.Git);
        Lease = DaemonLock.Acquire(_repo.Root);
        Proof = QuiescenceProof.Issue(
            "run-1", "attempt-1", SandboxRole.Coder, Workspace.AttemptRoot,
            "test harness: container stopped");
        Preconditions = new PromotionPreconditions(
            "work/WP-001", CandidateSha, MainSha, "WP-001: candidate [work package]");
    }

    private CandidateScanResult Scan() =>
        new CandidateScanner(_repo.Git).Scan(Workspace, Proof);

    private CandidatePromotionResult Promote(
        CandidatePromotionOptions? options = null,
        PromotionPreconditions? preconditions = null,
        QuiescenceProof? proof = null,
        DaemonLockLease? lease = null) =>
        Service.PromoteValidated(
            Workspace, proof ?? Proof, options ?? new CandidatePromotionOptions(),
            preconditions ?? Preconditions, lease ?? Lease);

    // ---- Section 2: no-follow reader semantics -----------------------------------------

    [Fact]
    public void Tracked_transient_file_unchanged_stays_exactly_baseline()
    {
        // Baseline contains the tracked transient file, committed BEFORE materialization.
        _repo.WriteFile(".aider.chat.history.md", "history v1\n");
        _repo.Git.CommitPaths([".aider.chat.history.md"], "track transient file");
        var newCandidate = _repo.Git.HeadSha();
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate,
                ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001",
                MainBaseSha = MainSha,
                Role = SandboxRole.Coder,
                RunId = "run-1",
                AttemptId = "attempt-2",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-2", SandboxRole.Coder, workspace.AttemptRoot, "test");
        var pre = new PromotionPreconditions(
            "work/WP-001", newCandidate, MainSha, "WP-001: candidate [work package]");
        var baselineOid = _repo.Git.ResolveTreeOfCommit(newCandidate);

        // Workspace untouched: no transient in the live walk, preserved in the target.
        var result = Service.PromoteValidated(
            workspace, proof, new CandidatePromotionOptions(), pre, Lease);

        Assert.True(result.NoChanges); // preserved entry is unchanged => no change record
        Assert.Equal(baselineOid, _repo.Git.ResolveTreeOfCommit(newCandidate));
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Tracked_transient_file_modified_or_deleted_stays_exactly_baseline()
    {
        _repo.WriteFile(".aider.chat.history.md", "history v1\n");
        _repo.Git.CommitPaths([".aider.chat.history.md"], "track transient file");
        var newCandidate = _repo.Git.HeadSha();
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = MainSha,
                Role = SandboxRole.Coder, RunId = "run-1", AttemptId = "attempt-3",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-3", SandboxRole.Coder, workspace.AttemptRoot, "test");
        var pre = new PromotionPreconditions(
            "work/WP-001", newCandidate, MainSha, "WP-001: candidate [work package]");

        // Agent MODIFIES then DELETES the tracked transient file: both must be ignored.
        File.WriteAllText(Path.Combine(workspace.SourcePath, ".aider.chat.history.md"),
            "agent rewrote the history\n");
        File.Delete(Path.Combine(workspace.SourcePath, ".aider.chat.history.md"));

        var result = Service.PromoteValidated(
            workspace, proof, new CandidatePromotionOptions(), pre, Lease);

        Assert.True(result.NoChanges);
        Assert.Equal(newCandidate, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        // The authoritative repository still holds the exact baseline blob.
        Assert.Equal("history v1\n",
            File.ReadAllText(Path.Combine(_repo.Root, ".aider.chat.history.md")));
    }

    [Fact]
    public void Tracked_transient_file_replaced_by_directory_stays_exactly_baseline()
    {
        _repo.WriteFile(".pi", "pi marker file\n");
        _repo.Git.CommitPaths([".pi"], "track transient file");
        var newCandidate = _repo.Git.HeadSha();
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = MainSha,
                Role = SandboxRole.Coder, RunId = "run-1", AttemptId = "attempt-4",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-4", SandboxRole.Coder, workspace.AttemptRoot, "test");
        var pre = new PromotionPreconditions(
            "work/WP-001", newCandidate, MainSha, "WP-001: candidate [work package]");

        // Agent replaces the transient FILE by a DIRECTORY with different content.
        File.Delete(Path.Combine(workspace.SourcePath, ".pi"));
        Directory.CreateDirectory(Path.Combine(workspace.SourcePath, ".pi"));
        File.WriteAllText(Path.Combine(workspace.SourcePath, ".pi", "state.json"), "{}");

        var result = Service.PromoteValidated(
            workspace, proof, new CandidatePromotionOptions(), pre, Lease);

        Assert.True(result.NoChanges);
        // The authoritative repository still has the exact baseline file.
        Assert.Equal("pi marker file\n", File.ReadAllText(Path.Combine(_repo.Root, ".pi")));
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Tracked_transient_subtree_variants_stay_exactly_baseline()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Root, ".pi"));
        _repo.WriteFile(".pi/session.json", "session v1\n");
        _repo.WriteFile(".pi/cache.bin", new byte[] { 1, 0, 2 });
        _repo.Git.CommitPaths([".pi/session.json", ".pi/cache.bin"], "track transient subtree");
        var newCandidate = _repo.Git.HeadSha();
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = MainSha,
                Role = SandboxRole.Coder, RunId = "run-1", AttemptId = "attempt-5",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-5", SandboxRole.Coder, workspace.AttemptRoot, "test");
        var pre = new PromotionPreconditions(
            "work/WP-001", newCandidate, MainSha, "WP-001: candidate [work package]");

        // Agent modifies one file and deletes the other inside the transient subtree.
        File.WriteAllText(Path.Combine(workspace.SourcePath, ".pi/session.json"), "hacked\n");
        File.Delete(Path.Combine(workspace.SourcePath, ".pi/cache.bin"));

        var result = Service.PromoteValidated(
            workspace, proof, new CandidatePromotionOptions(), pre, Lease);

        Assert.True(result.NoChanges);
        Assert.Equal("session v1\n",
            File.ReadAllText(Path.Combine(_repo.Root, ".pi/session.json")));
        Assert.True(File.Exists(Path.Combine(_repo.Root, ".pi/cache.bin")));
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Tracked_transient_subtree_replaced_by_file_stays_exactly_baseline()
    {
        Directory.CreateDirectory(Path.Combine(_repo.Root, ".pi"));
        _repo.WriteFile(".pi/session.json", "session v1\n");
        _repo.Git.CommitPaths([".pi/session.json"], "track transient subtree");
        var newCandidate = _repo.Git.HeadSha();
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = MainSha,
                Role = SandboxRole.Coder, RunId = "run-1", AttemptId = "attempt-6",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-6", SandboxRole.Coder, workspace.AttemptRoot, "test");
        var pre = new PromotionPreconditions(
            "work/WP-001", newCandidate, MainSha, "WP-001: candidate [work package]");

        // Agent deletes the whole subtree and replaces it with a plain FILE.
        File.Delete(Path.Combine(workspace.SourcePath, ".pi/session.json"));
        Directory.Delete(Path.Combine(workspace.SourcePath, ".pi"));
        File.WriteAllText(Path.Combine(workspace.SourcePath, ".pi"), "replaced by file\n");

        var result = Service.PromoteValidated(
            workspace, proof, new CandidatePromotionOptions(), pre, Lease);

        Assert.True(result.NoChanges);
        Assert.Equal("session v1\n",
            File.ReadAllText(Path.Combine(_repo.Root, ".pi/session.json")));
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Preserved_transient_keeps_exact_baseline_blob_and_mode_in_target_tree()
    {
        _repo.WriteFile(".pi/session.json", "session v1\n");
        _repo.Git.CommitPaths([".pi/session.json"], "track transient for oid check");
        var newCandidate = _repo.Git.HeadSha();
        var baselineBlobOid = _repo.HashObject(Encoding.UTF8.GetBytes("session v1\n"));
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = MainSha,
                Role = SandboxRole.Coder, RunId = "run-1", AttemptId = "attempt-7",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-7", SandboxRole.Coder, workspace.AttemptRoot, "test");

        // Agent deletes the transient file; the target tree must retain the baseline entry.
        File.Delete(Path.Combine(workspace.SourcePath, ".pi/session.json"));
        var scan = new CandidateScanner(_repo.Git).Scan(workspace, proof);

        var targetLsTree = TestGitRepo.RunGitInIsolatedEnv(
            workspace.TrustedIngestionPath, "ls-tree", "-r", scan.TargetTreeOid);
        Assert.Contains(baselineBlobOid, targetLsTree);
        Assert.Contains("100644", targetLsTree);
        Assert.Contains(".pi/session.json", targetLsTree);
        // No change record for the preserved path.
        Assert.DoesNotContain(scan.Changes, c => c.NormalizedPath == ".pi/session.json");
    }

    [Fact]
    public void Ordinary_lookalike_paths_are_scanned_as_ordinary_content()
    {
        // Nested/similarly named transient lookalikes are ORDINARY candidate content.
        WriteWorkspaceFile("src/.opencode", "ordinary nested file\n");
        WriteWorkspaceFile(".opencode-backup", "ordinary lookalike file\n");

        var scan = Scan();

        Assert.Contains(scan.Changes, c => c.NormalizedPath == "src/.opencode");
        Assert.Contains(scan.Changes, c => c.NormalizedPath == ".opencode-backup");
        Assert.False(scan.TargetEntries["src/.opencode"].ContentMayContainSecret);
    }

    private void WriteWorkspaceFile(string relative, string content)
    {
        var path = Path.Combine(Workspace.SourcePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Tracked_opencode_config_unchanged_returns_exact_no_change_preserving_baseline()
    {
        // The baseline tracks the exact .opencode/config.json; the live workspace is untouched.
        const string configContent = "{\n  \"model\": \"aider/quiet\"\n}\n";
        _repo.WriteFile(".opencode/config.json", configContent);
        _repo.Git.CommitPaths([".opencode/config.json"], "track opencode config");
        var newCandidate = _repo.Git.HeadSha();
        var baselineBlob = _repo.HashObject(Encoding.UTF8.GetBytes(configContent));
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = MainSha,
                Role = SandboxRole.Coder, RunId = "run-1", AttemptId = "attempt-8",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-8", SandboxRole.Coder, workspace.AttemptRoot, "test");
        var pre = new PromotionPreconditions(
            "work/WP-001", newCandidate, MainSha, "WP-001: candidate [work package]");

        var result = Service.PromoteValidated(
            workspace, proof, new CandidatePromotionOptions(), pre, Lease);

        // Exact no-change outcome: the tracked transient config is preserved, not applied.
        Assert.True(result.NoChanges);
        Assert.Null(result.CommitSha);
        Assert.Equal(newCandidate, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        // The exact baseline blob and mode survive in the authoritative tree, and the
        // authoritative worktree still holds the exact baseline bytes.
        var lsTree = _repo.Run("ls-tree", "-r", newCandidate);
        Assert.Contains($"100644 blob {baselineBlob}", lsTree);
        Assert.Contains(".opencode/config.json", lsTree);
        Assert.Equal(configContent,
            File.ReadAllText(Path.Combine(_repo.Root, ".opencode/config.json")));
    }

    [Fact]
    public void Tracked_transient_file_modified_at_scan_time_keeps_baseline_blob_and_mode_in_target()
    {
        // The baseline tracks the transient file; the live workspace keeps it MODIFIED.
        _repo.WriteFile(".pi/session.json", "session v1\n");
        _repo.Git.CommitPaths([".pi/session.json"], "track transient session");
        var newCandidate = _repo.Git.HeadSha();
        var baselineBlob = _repo.HashObject(Encoding.UTF8.GetBytes("session v1\n"));
        var workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = newCandidate, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = MainSha,
                Role = SandboxRole.Coder, RunId = "run-1", AttemptId = "attempt-9",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-9", SandboxRole.Coder, workspace.AttemptRoot, "test");

        File.WriteAllText(Path.Combine(workspace.SourcePath, ".pi/session.json"),
            "agent session v2\n");

        var scan = new CandidateScanner(_repo.Git).Scan(workspace, proof);

        // The live workspace STILL holds the modified transient content at scan time
        // (the scan never reads or reverts the live transient path).
        Assert.Equal("agent session v2\n",
            File.ReadAllText(Path.Combine(workspace.SourcePath, ".pi/session.json")));
        // The trusted target is exactly the baseline tree: the transient path retains the
        // baseline blob and mode, and no manifest change exists for it.
        Assert.Equal(workspace.BaselineTreeOid, scan.TargetTreeOid);
        var targetLsTree = TestGitRepo.RunGitInIsolatedEnv(
            workspace.TrustedIngestionPath, "ls-tree", "-r", scan.TargetTreeOid);
        Assert.Contains($"100644 blob {baselineBlob}", targetLsTree);
        Assert.Contains(".pi/session.json", targetLsTree);
        Assert.DoesNotContain(scan.Changes, c => c.NormalizedPath == ".pi/session.json");
    }

    // ---- Section 4: daemon-lock operation guard lifetime ---------------------------------

    [Fact]
    public void Disposed_lease_rejects_new_operation_guards()
    {
        using var otherRepo = new TestGitRepo();
        var lease = DaemonLock.Acquire(otherRepo.Root);
        lease.Dispose();
        Assert.True(lease.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => lease.BeginUseFor(otherRepo.Root));
    }

    [Fact]
    public void Last_operation_guard_releases_a_pending_disposal()
    {
        using var repo = new TestGitRepo();
        var lease = DaemonLock.Acquire(repo.Root);

        // Two nested guards keep the OS lock held even after disposal is requested.
        var guard1 = lease.BeginUseFor(repo.Root);
        var guard2 = lease.BeginUseFor(repo.Root);
        lease.Dispose();
        Assert.True(lease.IsDisposed);

        // The OS lock is STILL held: a second daemon cannot acquire.
        Assert.Throws<InvalidOperationException>(() => DaemonLock.Acquire(repo.Root));

        guard1.Dispose();
        Assert.Throws<InvalidOperationException>(() => DaemonLock.Acquire(repo.Root)); // guard2 active
        guard2.Dispose();

        // Last guard exited: the pending disposal completed and acquisition succeeds.
        using var reacquired = DaemonLock.Acquire(repo.Root);
        Assert.False(reacquired.IsDisposed);
    }

    [Fact]
    public void Lease_validates_canonical_git_identity_not_only_root_text()
    {
        using var repo = new TestGitRepo();
        using var other = new TestGitRepo();
        using var lease = DaemonLock.Acquire(repo.Root);

        // Different repository: rejected.
        Assert.Throws<InvalidOperationException>(() => lease.BeginUseFor(other.Root));

        // A differently-spelled but SAME location normalizes and passes the root check
        // while still validating the resolved canonical Git identity.
        var sameRootAlternateSpelling = repo.Root + "/.";
        using var guard = lease.BeginUseFor(sameRootAlternateSpelling);
        Assert.Equal(lease, guard.Lease);
    }

    [Fact]
    public void Promotion_keeps_OS_lock_held_when_outer_lease_is_disposed_concurrently()
    {
        var paused = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        Service.OperationPauseHook = () =>
        {
            paused.Set();          // promotion is now paused WITH its operation guard active
            release.Wait(TimeSpan.FromSeconds(30));
        };

        // The workspace has a real change so the paused promotion holds its guard through
        // scan, policy, patch build AND application while the outer lease is disposed.
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        var promoteTask = Task.Run(() => Promote());
        Assert.True(paused.Wait(TimeSpan.FromSeconds(30)),
            "promotion never reached its pause point");

        // The outer lease is disposed while promotion holds its own operation guard.
        Lease.Dispose();

        // The OS lock must STILL be held: a second daemon cannot acquire the same workspace.
        Assert.Throws<InvalidOperationException>(() => DaemonLock.Acquire(_repo.Root));

        release.Set();
        var result = promoteTask.Wait(TimeSpan.FromSeconds(60));
        Assert.True(result, "promotion did not finish after being released");
        var promoted = promoteTask.Result;
        Assert.False(promoted.NoChanges);
        Assert.Equal("modified by the agent\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/existing.txt")));

        // After promotion exited and the guard was released, acquisition succeeds.
        using var reacquired = DaemonLock.Acquire(_repo.Root);
        Assert.False(reacquired.IsDisposed);
    }

    [Fact]
    public void Foreign_repository_guard_is_rejected_before_any_application()
    {
        // Repository A (the fixture): a valid changed workspace, policy result and
        // validated patch, exactly as a real promotion would present them.
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        var scan = new CandidateScanner(_repo.Git).Scan(Workspace, Proof);
        PromotionPolicy.Evaluate(scan.Changes, new PromotionPolicyOptions(), scan.TargetEntries);
        var patch = new CandidatePatchBuilder().Build(
            Workspace, scan, new CandidatePromotionOptions().MaxPatchBytes);

        // Release A's daemon-lock lease: A is no longer protected by any retained lock
        // (reacquisition succeeds, proving the handle was really released).
        Lease.Dispose();
        Assert.True(Lease.IsDisposed);
        using (var reacquiredA = DaemonLock.Acquire(_repo.Root))
        {
            Assert.False(reacquiredA.IsDisposed);
        }

        // Repository B: a DIFFERENT repository with its own live lease and guard.
        using var repoB = new TestGitRepo();
        repoB.WriteFile("sentinel-b.txt", "repo b\n");
        repoB.Commit("initial on main b");
        using var leaseB = DaemonLock.Acquire(repoB.Root);
        using var guardB = leaseB.BeginUseFor(repoB.Root);
        Assert.Same(leaseB, guardB.Lease);

        // A's exact pre-attempt state.
        var branchA = _repo.Git.CurrentBranch();
        var headA = _repo.Git.HeadSha();
        var treeA = _repo.Git.ResolveTreeOfCommit(headA);

        // B's guard must NOT authorize applying to A: the identity rejection happens
        // BEFORE the audit patch is read and before any authoritative Git state is touched.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Service.ApplyValidated(Workspace, patch, Preconditions, guardB));
        Assert.Contains("different repository", ex.Message);

        // A is exactly unchanged: branch, HEAD, tree, clean index/worktree and contents.
        Assert.Equal("work/WP-001", _repo.Git.CurrentBranch());
        Assert.Equal(branchA, _repo.Git.CurrentBranch());
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.Equal(headA, _repo.Git.HeadSha());
        Assert.Equal(treeA, _repo.Git.ResolveTreeOfCommit(headA));
        Assert.True(_repo.Git.IsClean());
        Assert.Equal("original\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/existing.txt")));
        Assert.Equal("sentinel\n",
            File.ReadAllText(Path.Combine(_repo.Root, "sentinel.txt")));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/added-by-agent.txt")));
    }

    // ---- Section 5: commit-object / CAS flow and recovery --------------------------------

    [Fact]
    public void Rollback_after_ref_advance_restores_exact_pre_apply_state()
    {
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        WriteWorkspaceFile("src/added.txt", "added by the agent\n");
        Service.FaultInjection = CandidatePromotionFaultPoint.AfterRefAdvance;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Promote());
        }
        finally
        {
            Service.FaultInjection = CandidatePromotionFaultPoint.None;
        }
        Assert.Equal("work/WP-001", _repo.Git.CurrentBranch());
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(_repo.Root, "src/existing.txt")));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/added.txt")));
        Assert.Equal("sentinel\n", File.ReadAllText(Path.Combine(_repo.Root, "sentinel.txt")));
        Assert.Equal(_repo.Git.ResolveTreeOfCommit(CandidateSha), _repo.Git.WriteTree());
    }

    [Fact]
    public void Rollback_composite_failure_carries_both_errors()
    {
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        WriteWorkspaceFile("src/added.txt", "added by the agent\n");

        // Fault after the commit object was created and verified (BEFORE the work-branch
        // ref/HEAD advanced) + a decorator that fails the FIRST validated path restore:
        // both the promotion failure and the rollback failure must surface. (AfterCommit
        // is before ref advancement; AfterRefAdvance is after HEAD/ref advancement.)
        var failingRestore = new FailingRestoreGitService(_repo.Git, "src/existing.txt");
        var service = new CandidatePromotionService(failingRestore)
        {
            FaultInjection = CandidatePromotionFaultPoint.AfterCommit,
        };
        using var operation = Lease.BeginUseFor(_repo.Root);
        var scan = new CandidateScanner(_repo.Git).Scan(Workspace, Proof);
        PromotionPolicy.Evaluate(scan.Changes, new PromotionPolicyOptions(), scan.TargetEntries);
        var patch = new CandidatePatchBuilder().Build(
            Workspace, scan, new CandidatePromotionOptions().MaxPatchBytes);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            service.ApplyValidated(Workspace, patch, Preconditions, operation));
        Assert.Contains("unsafe for retry", ex.Message);
        var aggregate = Assert.IsType<AggregateException>(ex.InnerException);
        Assert.Equal(2, aggregate.InnerExceptions.Count);
    }
}

/// <summary>Decorator over the real GitService that fails the first validated-path restore,
/// used only to drive the composite rollback-failure path deterministically.</summary>
internal sealed class FailingRestoreGitService : IGitService
{
    private readonly IGitService _inner;
    private readonly string _failPath;
    private bool _failed;

    public FailingRestoreGitService(IGitService inner, string failPath)
    {
        _inner = inner;
        _failPath = failPath;
    }

    public string RepoPath => _inner.RepoPath;
    public bool IsRepository() => _inner.IsRepository();
    public void Init() => _inner.Init();
    public bool IsClean() => _inner.IsClean();
    public bool IsPathClean(string relativePath) => _inner.IsPathClean(relativePath);
    public string CurrentBranch() => _inner.CurrentBranch();
    public bool BranchExists(string branch) => _inner.BranchExists(branch);
    public void CreateAndCheckoutBranch(string branch) => _inner.CreateAndCheckoutBranch(branch);
    public void CheckoutBranch(string branch) => _inner.CheckoutBranch(branch);
    public void MergeMainIntoCurrentBranch() => _inner.MergeMainIntoCurrentBranch();
    public string? CommitAll(string message) => _inner.CommitAll(message);
    public string? CommitPaths(IEnumerable<string> relativePaths, string message) =>
        _inner.CommitPaths(relativePaths, message);
    public string SquashMergeToMain(string branch, string message) =>
        _inner.SquashMergeToMain(branch, message);
    public string DiffPatchAgainstMain(string branch, int maxChars = 20000) =>
        _inner.DiffPatchAgainstMain(branch, maxChars);
    public string HeadSha() => _inner.HeadSha();
    public string DiffHeadStat() => _inner.DiffHeadStat();
    public string DiffAgainstMain(string branch) => _inner.DiffAgainstMain(branch);
    public IReadOnlyList<GitCommit> RecentCommits(int count) => _inner.RecentCommits(count);
    public GitCommit? FindCommit(string shaOrRef) => _inner.FindCommit(shaOrRef);
    public void RevertCommitNoEdit(string sha) => _inner.RevertCommitNoEdit(sha);
    public bool IsAncestorOfMain(string sha) => _inner.IsAncestorOfMain(sha);
    public void DeleteBranchSafe(string branch, bool force = false) =>
        _inner.DeleteBranchSafe(branch, force);
    public string ResolveTreeOfCommit(string commitSha) => _inner.ResolveTreeOfCommit(commitSha);
    public byte[] LsTreeRecursiveRaw(string commitSha, long maxBytes) =>
        _inner.LsTreeRecursiveRaw(commitSha, maxBytes);
    public byte[] ReadBlobRaw(string objectSha, long maxBytes) =>
        _inner.ReadBlobRaw(objectSha, maxBytes);
    public long WriteBlobToFile(string objectSha, string destinationPath, long maxBytes) =>
        _inner.WriteBlobToFile(objectSha, destinationPath, maxBytes);
    public string HashObjectNoFilters(string filePath) => _inner.HashObjectNoFilters(filePath);
    public void UpdateIndexCacheInfo(string mode, string objectSha, string path) =>
        _inner.UpdateIndexCacheInfo(mode, objectSha, path);
    public void SetLocalConfig(string key, string value) => _inner.SetLocalConfig(key, value);
    public void RemoveFromIndex(string relativePath) => _inner.RemoveFromIndex(relativePath);
    public byte[] DiffTreesRaw(string oldTreeSha, string newTreeSha, long maxBytes) =>
        _inner.DiffTreesRaw(oldTreeSha, newTreeSha, maxBytes);
    public byte[] TreeDiffNamesRaw(string oldTreeSha, string newTreeSha, long maxBytes) =>
        _inner.TreeDiffNamesRaw(oldTreeSha, newTreeSha, maxBytes);
    public void VerifyPatchApplies(string patchFilePath) => _inner.VerifyPatchApplies(patchFilePath);
    public void ApplyPatchToIndexAndWorktree(string patchFilePath) =>
        _inner.ApplyPatchToIndexAndWorktree(patchFilePath);
    public void RestorePathFromCommit(string commitSha, string relativePath)
    {
        if (!_failed && relativePath == _failPath)
        {
            _failed = true;
            throw new InvalidOperationException("deterministic restore failure (test seam)");
        }
        _inner.RestorePathFromCommit(commitSha, relativePath);
    }
    public void ReadTreeEmpty() => _inner.ReadTreeEmpty();
    public byte[] StagedDiffRaw(string baselineTreeSha, long maxBytes) =>
        _inner.StagedDiffRaw(baselineTreeSha, maxBytes);
    public HashedIngestion HashObjectNoFiltersFromStream(
        Stream source, long maxBytes, int maxInspectedPrefixBytes) =>
        _inner.HashObjectNoFiltersFromStream(source, maxBytes, maxInspectedPrefixBytes);
    public void VerifyPatchBytesApplyToIndexAndWorktree(byte[] patchBytes) =>
        _inner.VerifyPatchBytesApplyToIndexAndWorktree(patchBytes);
    public void ApplyPatchBytesToIndexAndWorktree(byte[] patchBytes) =>
        _inner.ApplyPatchBytesToIndexAndWorktree(patchBytes);
    public void UpdateRefCompareAndSwap(string refName, string newSha, string expectedSha) =>
        _inner.UpdateRefCompareAndSwap(refName, newSha, expectedSha);
    public string CommitIndexWithTenninetyIdentity(string message) =>
        _inner.CommitIndexWithTenninetyIdentity(message);
    public string CreateCommitObjectForTree(string treeSha, string parentCommitSha, string message) =>
        _inner.CreateCommitObjectForTree(treeSha, parentCommitSha, message);
    public string ResolveCommitParent(string commitSha) => _inner.ResolveCommitParent(commitSha);
    public void StageAll() => _inner.StageAll();
    public string WriteTree() => _inner.WriteTree();
    public string? CommitStaged(string message) => _inner.CommitStaged(message);
    public string CommitAllowEmpty(string message) => _inner.CommitAllowEmpty(message);
}
