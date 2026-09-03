using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Phase 3 repair tests: stale-index immunity, NUL-delimited staged manifest cross-checks,
/// staged-byte secret scanning, non-forgeable proofs and patches, no-follow special-file
/// rejection, daemon-lock leasing, deterministic rollback at three fault points, Tenninety
/// commit identity and complete path-policy coverage.
/// </summary>
public class CandidatePromotionTests : IDisposable
{
    private readonly TestGitRepo _repo = new();
    private readonly TempDir _managedRoot = new();

    private CandidateWorkspace Workspace { get; }
    private string MainSha { get; }
    private string CandidateSha { get; }
    private CandidatePromotionService Service { get; }
    private DaemonLockLease Lease { get; }
    private QuiescenceProof Proof { get; }
    private PromotionPreconditions Preconditions { get; }
    private CandidateWorkspaceFactory Factory { get; }

    public CandidatePromotionTests()
    {
        _repo.WriteFile("sentinel.txt", "sentinel\n");
        _repo.Commit("initial on main");
        MainSha = _repo.Git.HeadSha();
        _repo.Git.CreateAndCheckoutBranch("work/WP-001");
        _repo.WriteFile("src/existing.txt", "original\n");
        _repo.WriteFile("src/binary.bin", new byte[] { 1, 2, 3, 0, 255 });
        _repo.WriteFile("src/doomed.txt", "delete me\n");
        CandidateSha = _repo.Commit("candidate");

        Factory = new CandidateWorkspaceFactory(_repo.Git);
        Workspace = CreateWorkspace(SandboxRole.Coder);
        Service = new CandidatePromotionService(_repo.Git);
        Lease = DaemonLock.Acquire(_repo.Root);
        Proof = QuiescenceProof.Issue(
            "run-1", "attempt-1", SandboxRole.Coder, Workspace.AttemptRoot,
            "test harness: container stopped");
        Preconditions = PreconditionsFor(CandidateSha);
    }

    private CandidateWorkspace CreateWorkspace(SandboxRole role) =>
        Factory.Create(new CandidateWorkspaceRequest
        {
            CommitSha = CandidateSha,
            ManagedRoot = _managedRoot.Root,
            WorkBranch = "work/WP-001",
            MainBaseSha = MainSha,
            Role = role,
            RunId = "run-1",
            AttemptId = "attempt-1",
        });

    private PromotionPreconditions PreconditionsFor(string baseSha) => new(
        "work/WP-001", baseSha, MainSha, "WP-001: candidate [work package]");

    private void WriteWorkspaceFile(string relative, string content)
    {
        var path = Path.Combine(Workspace.SourcePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string ApplyWithFixtureGuard(CandidateWorkspace ws, ValidatedCandidatePatch patch) =>
        ApplyWithGuard(ws, patch, Preconditions);

    private string ApplyWithGuard(
        CandidateWorkspace ws, ValidatedCandidatePatch patch, PromotionPreconditions pre)
    {
        using var operation = Lease.BeginUseFor(_repo.Root);
        return Service.ApplyValidated(ws, patch, pre, operation);
    }

    private void WriteWorkspaceFile(string relative, byte[] content)
    {
        var path = Path.Combine(Workspace.SourcePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private void DeleteWorkspaceFile(string relative) =>
        File.Delete(Path.Combine(Workspace.SourcePath, relative));

    private string WorktreePath(string relative) => Path.Combine(_repo.Root, relative);

    private CandidatePromotionResult Promote(
        CandidatePromotionOptions? options = null,
        PromotionPreconditions? preconditions = null,
        QuiescenceProof? proof = null,
        DaemonLockLease? lease = null) =>
        Service.PromoteValidated(
            Workspace, proof ?? Proof, options ?? new CandidatePromotionOptions(),
            preconditions ?? Preconditions, lease ?? Lease);

    // ---- B1: stale-index immunity ---------------------------------------------------

    [Fact]
    public void Rejected_addition_retry_starts_from_an_exact_fresh_index()
    {
        WriteWorkspaceFile("src/rejected.pem", "not even secret content");
        var rejected = Assert.Throws<CandidatePolicyRejectedException>(() => Promote());
        Assert.Contains("secret-shaped filename", rejected.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());

        // Retry with the rejected file deleted and an unrelated safe file added.
        DeleteWorkspaceFile("src/rejected.pem");
        WriteWorkspaceFile("src/safe.txt", "safe content\n");
        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.Equal(1, result.ChangedFileCount);
        var change = Assert.Single(result.Patch!.Changes);
        Assert.Equal("src/safe.txt", change.NormalizedPath);
        // The rejected addition is absent from the target tree, patch manifest, worktree,
        // index and commit.
        Assert.DoesNotContain(result.Patch.Changes, c => c.NormalizedPath.Contains("rejected"));
        var targetLsTree = TestGitRepo.RunGitInIsolatedEnv(
            Workspace.TrustedIngestionPath, "ls-tree", "-r", "--name-only",
            result.TargetTreeOid!);
        Assert.DoesNotContain("rejected.pem", targetLsTree);
        Assert.True(File.Exists(WorktreePath("src/safe.txt")));
        Assert.False(File.Exists(WorktreePath("src/rejected.pem")));
        var indexFiles = TestGitRepo.RunGitIn(
            _repo.Root, "ls-files", "--cached");
        Assert.DoesNotContain("rejected.pem", indexFiles);
        var commitLsTree = TestGitRepo.RunGitIn(
            _repo.Root, "ls-tree", "-r", "--name-only", "HEAD");
        Assert.DoesNotContain("rejected.pem", commitLsTree);
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Rejected_addition_retry_with_no_other_changes_returns_exact_baseline()
    {
        WriteWorkspaceFile("src/rejected.pem", "content");
        Assert.Throws<CandidatePolicyRejectedException>(() => Promote());
        DeleteWorkspaceFile("src/rejected.pem");

        var result = Promote();

        Assert.True(result.NoChanges);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Contaminated_ingestion_index_is_discarded_by_the_fresh_index_scan()
    {
        // Manually seed a non-baseline entry into the trusted ingestion index.
        var seededOid = _repo.HashObject(Encoding.UTF8.GetBytes("smuggled"));
        GitService.CreateDisposable(Workspace.TrustedIngestionPath)
            .UpdateIndexCacheInfo("100644", seededOid, "extra.txt");

        // The workspace itself is unchanged: the fresh-index scan must see NO changes and
        // the smuggled entry must not reach the target tree.
        var result = Promote();

        Assert.True(result.NoChanges);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Contaminated_ingestion_index_never_reaches_a_promoted_tree()
    {
        GitService.CreateDisposable(Workspace.TrustedIngestionPath)
            .UpdateIndexCacheInfo("100644",
                _repo.HashObject(Encoding.UTF8.GetBytes("smuggled")), "extra.txt");
        WriteWorkspaceFile("src/safe.txt", "safe content\n");

        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.DoesNotContain(result.Patch!.Changes,
            c => c.NormalizedPath == "extra.txt");
        var targetLsTree = TestGitRepo.RunGitInIsolatedEnv(
            Workspace.TrustedIngestionPath, "ls-tree", "-r", "--name-only",
            result.TargetTreeOid!);
        Assert.DoesNotContain("extra.txt", targetLsTree);
        Assert.False(File.Exists(WorktreePath("extra.txt")));
    }

    // ---- B2: NUL-delimited staged manifest cross-checks ------------------------------

    [Fact]
    public void Secret_scan_binds_to_the_exact_staged_bytes_not_the_workspace_path()
    {
        WriteWorkspaceFile("src/config.txt", "aws key AKIAIOSFODNN7EXAMPLE in content\n");
        var scan = new CandidateScanner(_repo.Git).Scan(Workspace, Proof);
        Assert.True(scan.TargetEntries["src/config.txt"].ContentMayContainSecret);

        // Replace the mutable workspace path with benign content AFTER ingestion: the policy
        // must still reject based on the exact staged bytes.
        WriteWorkspaceFile("src/config.txt", "totally benign\n");

        var ex = Assert.Throws<CandidatePolicyRejectedException>(() =>
            PromotionPolicy.Evaluate(scan.Changes, new PromotionPolicyOptions(),
                scan.TargetEntries));
        Assert.Contains("likely secret material", ex.Message);
        Assert.Contains("src/config.txt", ex.Message);

        // Positive control: a freshly scanned benign file passes.
        WriteWorkspaceFile("src/config.txt", "totally benign\n");
        var benignScan = new CandidateScanner(_repo.Git).Scan(Workspace, Proof);
        Assert.False(benignScan.TargetEntries["src/config.txt"].ContentMayContainSecret);
        PromotionPolicy.Evaluate(
            benignScan.Changes, new PromotionPolicyOptions(), benignScan.TargetEntries);
    }

    // ---- B4: patch integrity and bindings ---------------------------------------------

    [Fact]
    public void Patch_bytes_are_integrity_checked_at_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        var scan = new CandidateScanner(_repo.Git).Scan(Workspace, Proof);
        PromotionPolicy.Evaluate(scan.Changes, new PromotionPolicyOptions(), scan.TargetEntries);
        var patch = new CandidatePatchBuilder().Build(
            Workspace, scan, new CandidatePromotionOptions().MaxPatchBytes);

        // Tamper with the persisted audit copy (same length, different bytes): application
        // must reject on hash integrity before mutating HEAD, index or worktree.
        var tampered = new byte[patch.PatchByteLength];
        new Random(42).NextBytes(tampered);
        File.WriteAllBytes(patch.AuditFilePath, tampered);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ApplyWithFixtureGuard(Workspace, patch));
        Assert.Contains("SHA-256 integrity", ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        Assert.Equal("original\n", File.ReadAllText(WorktreePath("src/existing.txt")));
    }

    [Fact]
    public void Patch_bindings_are_enforced_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        var scan = new CandidateScanner(_repo.Git).Scan(Workspace, Proof);
        PromotionPolicy.Evaluate(scan.Changes, new PromotionPolicyOptions(), scan.TargetEntries);
        var patch = new CandidatePatchBuilder().Build(
            Workspace, scan, new CandidatePromotionOptions().MaxPatchBytes);

        // Workspace identity mismatch: a patch built for this workspace applied against a
        // different workspace object must reject.
        var otherWorkspace = CreateWorkspace(SandboxRole.Coder);
        Assert.Throws<InvalidOperationException>(() =>
            ApplyWithGuard(otherWorkspace, patch, Preconditions));

        // Precondition mismatches.
        Assert.Throws<InvalidOperationException>(() =>
            ApplyWithGuard(Workspace, patch,
                Preconditions with { MainBaseSha = new string('9', 40) }));
        Assert.Throws<InvalidOperationException>(() =>
            ApplyWithGuard(Workspace, patch,
                Preconditions with { WorkBranch = "work/OTHER" }));

        // Wrong branch checked out.
        _repo.Git.CheckoutBranch("main");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ApplyWithFixtureGuard(Workspace, patch));
        }
        finally
        {
            _repo.Git.CheckoutBranch("work/WP-001");
        }

        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        Assert.Equal("original\n", File.ReadAllText(WorktreePath("src/existing.txt")));
    }

    // ---- B5: non-forgeable, identity-bound quiescence proof ---------------------------

    [Fact]
    public void Quiescence_proofs_are_non_forgeable_and_identity_bound()
    {
        // No public constructor and no public factory returning a confirmed proof.
        Assert.Empty(typeof(QuiescenceProof).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(QuiescenceProof).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(QuiescenceProof)));

        // Replay/mismatch: a proof bound to another run, attempt, role or workspace must
        // reject before anything is scanned.
        foreach (var wrong in new[]
                 {
                     QuiescenceProof.Issue("other-run", "attempt-1", SandboxRole.Coder,
                         Workspace.AttemptRoot, "e"),
                     QuiescenceProof.Issue("run-1", "other-attempt", SandboxRole.Coder,
                         Workspace.AttemptRoot, "e"),
                     QuiescenceProof.Issue("run-1", "attempt-1", SandboxRole.Reviewer,
                         Workspace.AttemptRoot, "e"),
                     QuiescenceProof.Issue("run-1", "attempt-1", SandboxRole.Coder,
                         "/some/other/workspace", "e"),
                 })
        {
            Assert.Throws<InvalidOperationException>(() =>
                Service.PromoteValidated(
                    Workspace, wrong, new CandidatePromotionOptions(), Preconditions, Lease));
        }
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    // ---- B6: special files, no-follow semantics, hardlinks ------------------------------

    [Fact]
    public void Policy_and_patch_limits_are_enforced_independently()
    {
        WriteWorkspaceFile("one.txt", "1");
        WriteWorkspaceFile("two.txt", "2");

        var policyEx = Assert.Throws<CandidatePolicyRejectedException>(() =>
            Promote(new CandidatePromotionOptions
            {
                Policy = new PromotionPolicyOptions { MaxChangedFiles = 1 },
            }));
        Assert.Contains("exceeding the configured maximum of 1", policyEx.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());

        var patchEx = Assert.Throws<InvalidOperationException>(() =>
            Promote(new CandidatePromotionOptions { MaxPatchBytes = 10 }));
        Assert.Contains("maximum patch size", patchEx.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    // ---- B7: root .git versus nested .git; transients ----------------------------------

    [Fact]
    public void Always_rejected_metadata_can_never_be_allowlisted()
    {
        WriteWorkspaceFile(".tenninety/notes.txt", "metadata");
        WriteWorkspaceFile("tenninety-state.json", "{}");

        var allowEverything = new CandidatePromotionOptions
        {
            Policy = new PromotionPolicyOptions
            {
                AllowSensitivePaths = [".tenninety/notes.txt", "tenninety-state.json"],
            },
        };
        var ex = Assert.Throws<CandidatePolicyRejectedException>(() => Promote(allowEverything));
        Assert.Contains("protected Tenninety/git metadata path", ex.Message);
        Assert.Contains(".tenninety/notes.txt", ex.Message);
        Assert.Contains("tenninety-state.json", ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    // ---- B10: option validation on the no-change route ---------------------------------

    [Fact]
    public void Invalid_options_reject_before_scanning_even_without_changes()
    {
        var invalid = new CandidatePromotionOptions
        {
            Scan = new CandidateScanLimits { MaxFiles = 0 },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => Promote(invalid));
        Assert.Contains("MaxFiles must be within", ex.Message);

        var invalidPatch = new CandidatePromotionOptions { MaxPatchBytes = 0 };
        Assert.Throws<InvalidOperationException>(() => Promote(invalidPatch));

        var invalidPolicy = new CandidatePromotionOptions
        {
            Policy = new PromotionPolicyOptions { MaxChangedFiles = 0 },
        };
        Assert.Throws<InvalidOperationException>(() => Promote(invalidPolicy));

        // The workspace was never scanned: a subsequent valid promotion still works.
        var result = Promote();
        Assert.True(result.NoChanges);
    }

    // ---- B8: separate host preconditions -----------------------------------------------

    [Fact]
    public void Wrong_branch_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        // The workspace/preconditions name work/WP-001 but a different branch is checked out.
        _repo.Git.CheckoutBranch("main");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Promote());
            Assert.Contains("is not checked out", ex.Message);
        }
        finally
        {
            _repo.Git.CheckoutBranch("work/WP-001");
        }
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Wrong_head_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        // The binding check rejects a precondition that names a different base commit.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Promote(preconditions: Preconditions with { BaseCommitSha = MainSha }));
        Assert.Contains("do not describe this workspace", ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.Equal("original\n", File.ReadAllText(WorktreePath("src/existing.txt")));
    }

    [Fact]
    public void Moved_main_rejects_before_application_and_is_restored_by_the_fixture()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        _repo.Git.CheckoutBranch("main");
        _repo.WriteFile("main-advance.txt", "advanced\n");
        _repo.Git.CommitAll("advance main");
        var advancedSha = _repo.Git.HeadSha();
        _repo.Git.CheckoutBranch("work/WP-001");

        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("main is not at the recorded base SHA", ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());

        // Restore main so later fixtures start from the expected state.
        _repo.Git.UpdateRefCompareAndSwap("refs/heads/main", MainSha, advancedSha);
        Assert.Equal(MainSha, _repo.Git.FindCommit("main")!.Sha);
    }

    [Fact]
    public void Dirty_untracked_worktree_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        File.WriteAllText(WorktreePath("untracked.txt"), "uncommitted\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("must be clean", ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        File.Delete(WorktreePath("untracked.txt"));
    }

    [Fact]
    public void Dirty_tracked_worktree_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        File.WriteAllText(WorktreePath("sentinel.txt"), "modified without committing\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("must be clean", ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        File.WriteAllText(WorktreePath("sentinel.txt"), "sentinel\n");
    }

    [Fact]
    public void Dirty_index_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        File.WriteAllText(WorktreePath("sentinel.txt"), "staged change\n");
        _repo.Run("add", "sentinel.txt");
        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("must be clean", ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        _repo.Run("reset", "-q", "--", "sentinel.txt");
        File.WriteAllText(WorktreePath("sentinel.txt"), "sentinel\n");
    }

    // ---- B8: daemon-lock lease ----------------------------------------------------------

    [Fact]
    public void Promotion_requires_a_live_same_repository_lease()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");

        // Missing lease (direct call: no fixture fallback).
        Assert.Throws<ArgumentNullException>(() =>
            Service.PromoteValidated(
                Workspace, Proof, new CandidatePromotionOptions(), Preconditions, null!));

        // Disposed lease (acquired on another repository so the fixture lease is untouched).
        using var otherRepo = new TestGitRepo();
        var disposed = DaemonLock.Acquire(otherRepo.Root);
        disposed.Dispose();
        var disposedEx = Assert.Throws<InvalidOperationException>(() =>
            Promote(lease: disposed));
        Assert.Contains("disposed", disposedEx.Message);

        // Lease from a different repository.
        using var wrongLease = DaemonLock.Acquire(otherRepo.Root);
        var wrongEx = Assert.Throws<InvalidOperationException>(() =>
            Promote(lease: wrongLease));
        Assert.Contains("different repository", wrongEx.Message);

        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    // ---- B8: rollback at three deterministic fault points -------------------------------

    [Fact]
    public void Rollback_after_apply_mutations_restores_exact_pre_apply_state()
    {
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        WriteWorkspaceFile("src/added.txt", "added by the agent\n");
        Service.FaultInjection = CandidatePromotionFaultPoint.AfterApplyMutated;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Promote());
        }
        finally
        {
            Service.FaultInjection = CandidatePromotionFaultPoint.None;
        }
        AssertRollbackIsExact();
    }

    [Fact]
    public void Rollback_before_commit_restores_exact_pre_apply_state()
    {
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        WriteWorkspaceFile("src/added.txt", "added by the agent\n");
        Service.FaultInjection = CandidatePromotionFaultPoint.BeforeCommit;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Promote());
        }
        finally
        {
            Service.FaultInjection = CandidatePromotionFaultPoint.None;
        }
        AssertRollbackIsExact();
    }

    [Fact]
    public void Rollback_after_commit_restores_exact_pre_apply_state()
    {
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        WriteWorkspaceFile("src/added.txt", "added by the agent\n");
        Service.FaultInjection = CandidatePromotionFaultPoint.AfterCommit;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Promote());
        }
        finally
        {
            Service.FaultInjection = CandidatePromotionFaultPoint.None;
        }
        // HEAD had already advanced; the compare-and-swap ref update moved it back.
        AssertRollbackIsExact();
    }

    private void AssertRollbackIsExact()
    {
        Assert.Equal("work/WP-001", _repo.Git.CurrentBranch());
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        Assert.Equal("original\n", File.ReadAllText(WorktreePath("src/existing.txt")));
        Assert.False(File.Exists(WorktreePath("src/added.txt")));
        Assert.Equal("sentinel\n", File.ReadAllText(WorktreePath("sentinel.txt")));
        Assert.Equal(_repo.Git.ResolveTreeOfCommit(CandidateSha), _repo.Git.WriteTree());
    }

    // ---- B8: Tenninety commit identity ---------------------------------------------------

    [Fact]
    public void Promotion_commits_use_tenninety_identity_and_trusted_message()
    {
        _repo.Git.SetLocalConfig("user.name", "someone-else");
        _repo.Git.SetLocalConfig("user.email", "someone-else@example.com");
        WriteWorkspaceFile("src/existing.txt", "modified\n");

        var result = Promote();

        Assert.False(result.NoChanges);
        var identity = TestGitRepo.RunGitIn(
            _repo.Root, "log", "-1", "--format=%an%x1f%ae%x1f%cn%x1f%ce");
        var parts = identity.Trim().Split('\x1f');
        Assert.Equal("tenninety", parts[0]);
        Assert.Equal("tenninety@localhost", parts[1]);
        Assert.Equal("tenninety", parts[2]);
        Assert.Equal("tenninety@localhost", parts[3]);
        Assert.Contains("WP-001: candidate [work package]", _repo.Git.RecentCommits(1)[0].Subject);
    }

    // ---- B5/21: no public bypass ----------------------------------------------------------

    [Fact]
    public void No_public_bypass_exists_for_trust_boundaries()
    {
        // Quiescence proof: no public constructor, no public confirmed factory.
        Assert.Empty(typeof(QuiescenceProof).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance));
        Assert.Empty(typeof(QuiescenceProof).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(QuiescenceProof)));

        // Candidate workspace: no public constructor; get-only typed role.
        Assert.Empty(typeof(CandidateWorkspace).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance));
        var roleProperty = typeof(CandidateWorkspace).GetProperty(nameof(CandidateWorkspace.Role))!;
        Assert.False(roleProperty.CanWrite);
        Assert.Equal(typeof(SandboxRole), roleProperty.PropertyType);

        // The actionable validated patch and the apply entry point are not public.
        Assert.False(typeof(ValidatedCandidatePatch).IsPublic);
        Assert.All(typeof(CandidatePromotionService).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            m => Assert.NotEqual(nameof(CandidatePromotionService.ApplyValidated), m.Name));

        // A reviewer workspace cannot be relabeled: the role is fixed at construction and
        // promotion refuses it.
        var reviewer = CreateWorkspace(SandboxRole.Reviewer);
        var reviewerProof = QuiescenceProof.Issue(
            "run-1", "attempt-1", SandboxRole.Reviewer, reviewer.AttemptRoot, "e");
        Assert.Throws<InvalidOperationException>(() =>
            Service.PromoteValidated(
                reviewer, reviewerProof, new CandidatePromotionOptions(), Preconditions, Lease));
    }

    public void Dispose()
    {
        Lease.Dispose();
        _managedRoot.Dispose();
        _repo.Dispose();
    }
}
