using System.Text;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Explicit independent happy-path and rejection tests for the complete Phase 3 promotion
/// flow (Section 6 of the repair prompt). Each test isolates the property it names; each
/// rejection test proves nothing was applied.
/// </summary>
public class CandidatePromotionHappyPathTests : IDisposable
{
    private readonly TestGitRepo _repo = new();
    private readonly TempDir _managedRoot = new();
    private readonly string _mainSha;
    private readonly string _candidateSha;
    private readonly CandidateWorkspace _workspace;
    private readonly CandidatePromotionService _service;
    private readonly DaemonLockLease _lease;
    private readonly QuiescenceProof _proof;
    private readonly PromotionPreconditions _pre;

    public CandidatePromotionHappyPathTests()
    {
        _repo.WriteFile("sentinel.txt", "sentinel\n");
        _repo.Commit("initial on main");
        _mainSha = _repo.Git.HeadSha();
        _repo.Git.CreateAndCheckoutBranch("work/WP-001");
        _repo.WriteFile("src/existing.txt", "original\n");
        _repo.WriteFile("src/doomed.txt", "delete me\n");
        _repo.WriteFile("src/binary.bin", new byte[] { 1, 2, 3, 0, 255 });
        _candidateSha = _repo.Commit("candidate");
        _workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = _candidateSha,
                ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001",
                MainBaseSha = _mainSha,
                Role = SandboxRole.Coder,
                RunId = "run-1",
                AttemptId = "attempt-1",
            });
        _service = new CandidatePromotionService(_repo.Git);
        _lease = DaemonLock.Acquire(_repo.Root);
        _proof = QuiescenceProof.Issue(
            "run-1", "attempt-1", SandboxRole.Coder, _workspace.AttemptRoot,
            "test harness: container stopped");
        _pre = new PromotionPreconditions(
            "work/WP-001", _candidateSha, _mainSha, "WP-001: candidate [work package]");
    }

    public void Dispose()
    {
        _lease.Dispose();
        _managedRoot.Dispose();
        _repo.Dispose();
    }

    private CandidatePromotionResult Promote(
        CandidatePromotionOptions? options = null,
        PromotionPreconditions? preconditions = null) =>
        _service.PromoteValidated(_workspace, _proof, options ?? new CandidatePromotionOptions(),
            preconditions ?? _pre, _lease);

    private static byte[] PatchBytesOf(ValidatedCandidatePatch patch) =>
        File.ReadAllBytes(patch.AuditFilePath);

    private void WriteWorkspaceFile(string relative, string content)
    {
        var path = Path.Combine(_workspace.SourcePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteWorkspaceFile(string relative, byte[] content)
    {
        var path = Path.Combine(_workspace.SourcePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    // 1. No-change result from exact tree equality.
    [Fact]
    public void No_change_detected_from_tree_equality()
    {
        var result = Promote();
        Assert.True(result.NoChanges);
        Assert.Null(result.CommitSha);
        Assert.Null(result.Patch);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    // 2. Text add + modify + delete in one promotion.
    [Fact]
    public void Text_add_modify_delete_in_one_promotion()
    {
        WriteWorkspaceFile("src/existing.txt", "modified by the agent\n");
        WriteWorkspaceFile("src/new-text.txt", "added text\n");
        File.Delete(Path.Combine(_workspace.SourcePath, "src/doomed.txt"));

        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.Equal(3, result.ChangedFileCount);
        Assert.NotEqual(_candidateSha, result.CommitSha);
        // One commit whose parent is the candidate.
        Assert.Equal(_candidateSha, _repo.Git.ResolveCommitParent(result.CommitSha!));
        Assert.Equal(result.TargetTreeOid, _repo.Git.ResolveTreeOfCommit(result.CommitSha!));
        Assert.Equal("modified by the agent\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/existing.txt")));
        Assert.Equal("added text\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/new-text.txt")));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/doomed.txt")));
        Assert.True(_repo.Git.IsClean());
    }

    // 3. Binary add + modify + delete byte-for-byte (incl. NUL/non-text bytes).
    [Fact]
    public void Binary_add_modify_delete_byte_for_byte()
    {
        var modified = new byte[] { 0, 255, 0, 7, 128, 64, 0 };
        var added = new byte[256];
        for (var i = 0; i < added.Length; i++) added[i] = (byte)(i * 7 % 256);
        File.WriteAllBytes(Path.Combine(_workspace.SourcePath, "src/binary.bin"), modified);
        File.WriteAllBytes(Path.Combine(_workspace.SourcePath, "src/added.bin"), added);
        File.Delete(Path.Combine(_workspace.SourcePath, "src/doomed.txt"));

        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.True(modified.AsSpan().SequenceEqual(
            File.ReadAllBytes(Path.Combine(_repo.Root, "src/binary.bin"))));
        Assert.True(added.AsSpan().SequenceEqual(
            File.ReadAllBytes(Path.Combine(_repo.Root, "src/added.bin"))));
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/doomed.txt")));
    }

    // 4. Executable-mode change: exact git mode via ls-tree.
    [Fact]
    public void Executable_mode_change_promotes_with_exact_git_mode()
    {
        var path = Path.Combine(_workspace.SourcePath, "src/existing.txt");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var result = Promote();

        Assert.False(result.NoChanges);
        var lsTree = TestGitRepo.RunGitIn(_repo.Root, "ls-tree", "HEAD", "--", "src/existing.txt");
        Assert.Contains("100755", lsTree);
        Assert.DoesNotContain("100644", lsTree);
    }

    // 5. Rename: one deletion and one addition, no rename status.
    [Fact]
    public void Rename_manifest_contains_deletion_and_addition_only()
    {
        File.Move(Path.Combine(_workspace.SourcePath, "src/existing.txt"),
            Path.Combine(_workspace.SourcePath, "src/renamed.txt"));

        var result = Promote();

        Assert.NotNull(result.Patch);
        Assert.Contains(result.Patch.Changes,
            c => c.Kind == GitChangeKind.Deleted && c.NormalizedPath == "src/existing.txt");
        Assert.Contains(result.Patch.Changes,
            c => c.Kind == GitChangeKind.Added && c.NormalizedPath == "src/renamed.txt");
        Assert.DoesNotContain(result.Patch.Changes, c => c.Kind == GitChangeKind.TypeChanged);
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/existing.txt")));
        Assert.Equal("original\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/renamed.txt")));
    }

    // 6. Disposable root .git tampering ignored; ordinary change promotes; no .git byte leaks.
    [Fact]
    public void Disposable_git_tampering_ignored_while_ordinary_change_promotes()
    {
        File.WriteAllText(Path.Combine(_workspace.SourcePath, ".git", "HEAD"), "junk");
        File.WriteAllBytes(Path.Combine(_workspace.SourcePath, ".git", "smuggled.bin"),
            new byte[] { 0, 1, 2 });
        WriteWorkspaceFile("src/existing.txt", "legitimately modified\n");

        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.Equal("legitimately modified\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/existing.txt")));
        var commitLsTree = TestGitRepo.RunGitIn(
            _repo.Root, "ls-tree", "-r", "--name-only", "HEAD");
        Assert.DoesNotContain("smuggled", commitLsTree);
        Assert.DoesNotContain(".git", commitLsTree);
    }

    // 7/8. Reviewer and Tester workspaces rejected before scan/apply (separate tests).
    [Fact]
    public void Reviewer_workspace_rejected_before_scan_and_apply()
    {
        var reviewer = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = _candidateSha, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = _mainSha,
                Role = SandboxRole.Reviewer, RunId = "run-1", AttemptId = "attempt-r",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-r", SandboxRole.Reviewer, reviewer.AttemptRoot, "test");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PromoteValidated(
                reviewer, proof, new CandidatePromotionOptions(), _pre, _lease));
        Assert.Contains("reviewer", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Tester_workspace_rejected_before_scan_and_apply()
    {
        var tester = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = _candidateSha, ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001", MainBaseSha = _mainSha,
                Role = SandboxRole.Tester, RunId = "run-1", AttemptId = "attempt-t",
            });
        var proof = QuiescenceProof.Issue(
            "run-1", "attempt-t", SandboxRole.Tester, tester.AttemptRoot, "test");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.PromoteValidated(
                tester, proof, new CandidatePromotionOptions(), _pre, _lease));
        Assert.Contains("tester", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    // 9. Protected metadata rejects the entire candidate.
    [Fact]
    public void Protected_metadata_rejects_entire_candidate()
    {
        WriteWorkspaceFile(".tenninety/attempt-state.json", "{}");
        WriteWorkspaceFile("src/fine.txt", "fine\n");

        var ex = Assert.Throws<CandidatePolicyRejectedException>(() => Promote());
        Assert.Contains("protected Tenninety/git metadata path", ex.Message);
        Assert.False(File.Exists(Path.Combine(_repo.Root, "src/fine.txt")));
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
    }

    // 10. Sensitive paths reject unless exactly allowlisted.
    [Fact]
    public void Sensitive_path_rejects_unless_exact_normalized_path_allowlisted()
    {
        WriteWorkspaceFile("NuGet.config", "<configuration />");

        var ex = Assert.Throws<CandidatePolicyRejectedException>(() => Promote());
        Assert.Contains("sensitive path", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());

        var allowed = new CandidatePromotionOptions
        {
            Policy = new PromotionPolicyOptions { AllowSensitivePaths = ["NuGet.config"] },
        };
        var result = Promote(allowed);
        Assert.False(result.NoChanges);
        Assert.True(File.Exists(Path.Combine(_repo.Root, "NuGet.config")));
    }

    // 11. Secret filename and bounded secret content each reject the entire candidate.
    [Fact]
    public void Secret_filename_rejects_entire_candidate()
    {
        WriteWorkspaceFile("src/id_rsa", "key data");
        var ex = Assert.Throws<CandidatePolicyRejectedException>(() => Promote());
        Assert.Contains("secret-shaped filename", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Secret_content_rejects_entire_candidate()
    {
        WriteWorkspaceFile("src/settings.txt", "token: ghp_" + new string('a', 36) + "\n");
        var ex = Assert.Throws<CandidatePolicyRejectedException>(() => Promote());
        Assert.Contains("likely secret material", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
    }

    // 12. Isolated precondition rejections (separate fixtures per condition).
    [Fact]
    public void Wrong_branch_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        _repo.Git.CheckoutBranch("main");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => Promote());
            Assert.Contains("is not checked out", ex.Message);
        }
        finally { _repo.Git.CheckoutBranch("work/WP-001"); }
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Base_sha_mismatch_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        var wrong = _pre with { BaseCommitSha = _mainSha };
        var ex = Assert.Throws<InvalidOperationException>(() => Promote(
            preconditions: wrong));
        Assert.Contains("do not describe this workspace", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Dirty_tracked_worktree_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        File.WriteAllText(Path.Combine(_repo.Root, "sentinel.txt"), "dirty\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("must be clean", ex.Message);
        File.WriteAllText(Path.Combine(_repo.Root, "sentinel.txt"), "sentinel\n");
    }

    [Fact]
    public void Dirty_untracked_worktree_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        File.WriteAllText(Path.Combine(_repo.Root, "untracked.txt"), "new\n");
        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("must be clean", ex.Message);
        File.Delete(Path.Combine(_repo.Root, "untracked.txt"));
    }

    [Fact]
    public void Dirty_index_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        File.WriteAllText(Path.Combine(_repo.Root, "sentinel.txt"), "staged\n");
        _repo.Run("add", "sentinel.txt");
        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("must be clean", ex.Message);
        _repo.Run("reset", "-q", "--", "sentinel.txt");
        File.WriteAllText(Path.Combine(_repo.Root, "sentinel.txt"), "sentinel\n");
    }

    [Fact]
    public void Advanced_main_rejects_before_application()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        _repo.Git.CheckoutBranch("main");
        _repo.WriteFile("advance.txt", "adv\n");
        _repo.Git.CommitAll("advance main");
        var advanced = _repo.Git.HeadSha();
        _repo.Git.CheckoutBranch("work/WP-001");

        var ex = Assert.Throws<InvalidOperationException>(() => Promote());
        Assert.Contains("main is not at the recorded base SHA", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());

        _repo.Git.UpdateRefCompareAndSwap("refs/heads/main", _mainSha, advanced);
    }

    // 13. Deliberate target-tree mismatch aborts, exact pre-apply state.
    [Fact]
    public void Target_tree_mismatch_aborts_leaving_exact_pre_apply_state()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        var scan = new CandidateScanner(_repo.Git).Scan(_workspace, _proof);
        PromotionPolicy.Evaluate(scan.Changes, new PromotionPolicyOptions(), scan.TargetEntries);
        var patch = new CandidatePatchBuilder().Build(
            _workspace, scan, new CandidatePromotionOptions().MaxPatchBytes);
        var tampered = new ValidatedCandidatePatch(_workspace, patch.BaseTreeOid,
            new string('f', 40), PatchBytesOf(patch), patch.AuditFilePath, patch.Changes);

        using var operation = _lease.BeginUseFor(_repo.Root);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _service.ApplyValidated(_workspace, tampered, _pre, operation));
        Assert.Contains("target tree", ex.Message);
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        Assert.Equal("original\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/existing.txt")));
    }

    // 14. Successful promotion: parent = candidate, tree = trusted target.
    [Fact]
    public void Successful_promotion_parent_is_candidate_and_tree_is_target()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.Equal(_candidateSha, _repo.Git.ResolveCommitParent(result.CommitSha!));
        Assert.Equal(result.TargetTreeOid, _repo.Git.ResolveTreeOfCommit(result.CommitSha!));
    }

    // 15. Valid odd filename survives manifest and promotion exactly.
    [Fact]
    public void Odd_filename_with_spaces_and_parentheses_survives_promotion()
    {
        WriteWorkspaceFile("src/name with spaces (v1).txt", "odd name content\n");
        var result = Promote();
        Assert.False(result.NoChanges);
        Assert.Contains(result.Patch!.Changes,
            c => c.NormalizedPath == "src/name with spaces (v1).txt");
        Assert.Equal("odd name content\n",
            File.ReadAllText(Path.Combine(_repo.Root, "src/name with spaces (v1).txt")));
    }

    // 16. Application failure leaves the authoritative checkout exact, clean, unchanged.
    [Fact]
    public void Application_failure_leaves_checkout_exact_clean_unchanged()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        var scan = new CandidateScanner(_repo.Git).Scan(_workspace, _proof);
        PromotionPolicy.Evaluate(scan.Changes, new PromotionPolicyOptions(), scan.TargetEntries);
        var patch = new CandidatePatchBuilder().Build(
            _workspace, scan, new CandidatePromotionOptions().MaxPatchBytes);
        var tampered = new ValidatedCandidatePatch(_workspace, patch.BaseTreeOid,
            new string('e', 40), PatchBytesOf(patch), patch.AuditFilePath, patch.Changes);

        using var operation = _lease.BeginUseFor(_repo.Root);
        Assert.Throws<InvalidOperationException>(() =>
            _service.ApplyValidated(_workspace, tampered, _pre, operation));

        Assert.Equal("work/WP-001", _repo.Git.CurrentBranch());
        Assert.Equal(_candidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
        Assert.Equal(_repo.Git.ResolveTreeOfCommit(_candidateSha), _repo.Git.WriteTree());
    }
}
