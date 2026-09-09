using Tenninety.Git;

namespace Tenninety.Tests;

public class GitServiceTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly GitService _git;

    public GitServiceTests()
    {
        _git = new GitService(_tmp.Root);
        _git.Init();
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "README.md"), "hello\n");
        _git.CommitAll("initial");
    }

    [Fact]
    public void Init_creates_repo_on_main()
    {
        Assert.True(_git.IsRepository());
        Assert.Equal("main", _git.CurrentBranch());
    }

    [Fact]
    public void Fast_forward_merge_promotes_work_branch()
    {
        _git.CreateAndCheckoutBranch("work/WP-001");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "a.txt"), "A");
        _git.CommitAll("WP-001 change");
        var branchTip = _git.HeadSha();

        var mergeSha = _git.SquashMergeToMain("work/WP-001", "WP-001: change");

        Assert.Equal("main", _git.CurrentBranch());
        Assert.Equal(mergeSha, _git.HeadSha());
        Assert.NotEqual(branchTip, mergeSha); // squashed into one NEW commit
        Assert.True(File.ReadAllText(System.IO.Path.Combine(_tmp.Root, "a.txt")) == "A");
        Assert.Contains(_git.RecentCommits(5), c => c.Subject == "WP-001: change");
    }

    [Fact]
    public void Diverged_branch_falls_back_to_squash_merge()
    {
        _git.CreateAndCheckoutBranch("work/WP-002");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "b.txt"), "B");
        _git.CommitAll("WP-002 work");

        // Diverge main with a parallel commit.
        _git.CheckoutBranch("main");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "main-only.txt"), "M");
        _git.CommitAll("parallel main commit");

        var mergeSha = _git.SquashMergeToMain("work/WP-002", "WP-002: work");

        Assert.Equal("main", _git.CurrentBranch());
        Assert.True(File.Exists(System.IO.Path.Combine(_tmp.Root, "b.txt")));
        Assert.True(File.Exists(System.IO.Path.Combine(_tmp.Root, "main-only.txt")));
        Assert.Contains(_git.RecentCommits(50), c => c.Subject == "WP-002: work");
        _ = mergeSha;
    }

    [Fact]
    public void Revert_creates_inverse_commit_without_history_rewrite()
    {
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "feature.txt"), "x");
        _git.CommitAll("add feature");
        var featureSha = _git.HeadSha();

        _git.RevertCommitNoEdit(featureSha);

        Assert.False(File.Exists(System.IO.Path.Combine(_tmp.Root, "feature.txt")));
        Assert.NotEqual(featureSha, _git.HeadSha());
        Assert.Contains(_git.RecentCommits(5), c => c.Subject.StartsWith("Revert \"add feature\""));
    }

    [Fact]
    public void Branch_safety_refuses_reuse_and_safe_delete()
    {
        _git.CreateAndCheckoutBranch("work/WP-003");
        Assert.Throws<InvalidOperationException>(() => _git.CreateAndCheckoutBranch("work/WP-003"));

        _git.CheckoutBranch("main");
        _git.DeleteBranchSafe("work/WP-003");
        Assert.False(_git.BranchExists("work/WP-003"));
    }

    [Fact]
    public void Find_commit_resolves_refs()
    {
        var head = _git.FindCommit("HEAD");
        Assert.NotNull(head);
        Assert.Equal(head!.Sha, _git.FindCommit(head.Sha[..8])!.Sha);
        Assert.Null(_git.FindCommit("definitely-not-a-sha"));
    }

    [Fact]
    public void Find_commit_treats_dash_prefixed_input_as_a_revision_not_an_option()
    {
        Assert.NotNull(_git.FindCommit("HEAD"));
        Assert.Null(_git.FindCommit("-p"));
    }

    [Fact]
    public void Commit_all_excludes_new_secret_shaped_files()
    {
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, ".env"), "API_KEY=do-not-commit");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "id_rsa"), "private-key");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "#secret.env"), "comment-shaped-name");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "Secrets.JSON"), "case-sensitive-ignore");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "safe.txt"), "safe");

        _git.CommitAll("safe change");

        var committed = RunGit("ls-tree", "-r", "--name-only", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("safe.txt", committed);
        Assert.DoesNotContain(".env", committed);
        Assert.DoesNotContain("id_rsa", committed);
        Assert.DoesNotContain("#secret.env", committed);
        Assert.DoesNotContain("Secrets.JSON", committed);
        Assert.True(_git.IsClean());
    }

    [Fact]
    public void Commit_paths_does_not_capture_unrelated_staged_work()
    {
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "user.txt"), "user work");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "framework.txt"), "framework work");
        RunGit("add", "--", "user.txt");

        _git.CommitPaths(["framework.txt"], "framework commit");

        var committed = RunGit("ls-tree", "-r", "--name-only", "HEAD")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("framework.txt", committed);
        Assert.DoesNotContain("user.txt", committed);
        Assert.Contains("user.txt", RunGit("diff", "--cached", "--name-only"));
    }

    [Fact]
    public void Squash_promotion_refuses_a_dirty_work_branch()
    {
        _git.CreateAndCheckoutBranch("work/WP-004");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "reviewed.txt"), "reviewed");
        _git.CommitAll("reviewed change");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "post-review.txt"), "not reviewed");

        Assert.Throws<InvalidOperationException>(
            () => _git.SquashMergeToMain("work/WP-004", "must not promote"));
        Assert.Equal("work/WP-004", _git.CurrentBranch());
    }

    [Fact]
    public void Signing_failure_during_preparation_leaves_branch_head_index_and_worktree_unchanged()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        _git.CreateAndCheckoutBranch("work/WP-005");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "signed.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        var main = _git.FindCommit("main")!.Sha;
        var index = RunGit("write-tree");
        var signer = System.IO.Path.Combine(_tmp.Root, ".git", "failing-signer");
        File.WriteAllText(signer, "#!/bin/sh\nexit 73\n");
        File.SetUnixFileMode(signer,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _git.SetLocalConfig("commit.gpgsign", "true");
        _git.SetLocalConfig("gpg.program", signer);

        Assert.Throws<GitException>(() =>
            _git.SquashMergeToMain("work/WP-005", "WP-005: signed"));

        Assert.Equal("work/WP-005", _git.CurrentBranch());
        Assert.Equal(candidate, _git.HeadSha());
        Assert.Equal(main, _git.FindCommit("main")!.Sha);
        Assert.Equal(index, RunGit("write-tree"));
        Assert.Empty(RunGit("status", "--porcelain"));
        Assert.Equal("reviewed\n", File.ReadAllText(System.IO.Path.Combine(_tmp.Root, "signed.txt")));
    }

    [Fact]
    public void Published_promotion_recovers_forward_and_can_be_repeated()
    {
        _git.CreateAndCheckoutBranch("work/WP-006");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "recover.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        var main = _git.FindCommit("main")!.Sha;
        var promotion = _git.PrepareSquashPromotion(
            "work/WP-006", main, candidate, "WP-006: recover");
        _git.SquashPromotionFailpoint = phase =>
        {
            if (phase == SquashPromotionPhase.AfterMainCompareAndSwap)
                throw new IOException("simulated failure after publication");
        };

        var error = Assert.Throws<SquashPromotionException>(() =>
            _git.CompleteSquashPromotion("work/WP-006", main, candidate, promotion));

        Assert.True(error.RecoverySucceeded);
        Assert.Contains("simulated failure after publication", error.Message);
        Assert.Equal("main", _git.CurrentBranch());
        Assert.Equal(promotion, _git.HeadSha());
        Assert.True(_git.IsClean());
        _git.SquashPromotionFailpoint = null;
        _git.CompleteSquashPromotion("work/WP-006", main, candidate, promotion);
        Assert.Equal(promotion, _git.HeadSha());
        Assert.True(_git.IsClean());
    }

    [Fact]
    public void Recovery_failure_reports_both_failures_and_quarantines_exact_partial_state()
    {
        _git.CreateAndCheckoutBranch("work/WP-007");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "quarantine.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        var main = _git.FindCommit("main")!.Sha;
        var candidateIndex = RunGit("write-tree");
        var promotion = _git.PrepareSquashPromotion(
            "work/WP-007", main, candidate, "WP-007: quarantine");
        _git.SquashPromotionFailpoint = phase =>
        {
            if (phase == SquashPromotionPhase.AfterMainCompareAndSwap)
                throw new IOException("original publication follow-up failure");
            if (phase == SquashPromotionPhase.BeforeRecovery)
                throw new UnauthorizedAccessException("recovery failure");
        };

        var error = Assert.Throws<SquashPromotionException>(() =>
            _git.CompleteSquashPromotion("work/WP-007", main, candidate, promotion));

        Assert.False(error.RecoverySucceeded);
        Assert.Contains("original publication follow-up failure", error.Message);
        Assert.Contains("recovery failure", error.Message);
        Assert.Equal(promotion, _git.FindCommit("main")!.Sha);
        Assert.Equal("work/WP-007", _git.CurrentBranch());
        Assert.Equal(candidate, _git.HeadSha());
        Assert.Equal(candidateIndex, RunGit("write-tree"));
        Assert.Empty(RunGit("status", "--porcelain"));
    }

    [Fact]
    public void External_work_appearing_after_publication_is_preserved_and_quarantined()
    {
        _git.CreateAndCheckoutBranch("work/WP-008");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "candidate.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        var main = _git.FindCommit("main")!.Sha;
        var promotion = _git.PrepareSquashPromotion(
            "work/WP-008", main, candidate, "WP-008: preserve external");
        _git.SquashPromotionFailpoint = phase =>
        {
            if (phase != SquashPromotionPhase.AfterMainCompareAndSwap) return;
            File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "external-untracked.txt"), "preserve\n");
            throw new IOException("failure after publication");
        };

        var error = Assert.Throws<SquashPromotionException>(() =>
            _git.CompleteSquashPromotion("work/WP-008", main, candidate, promotion));

        Assert.False(error.RecoverySucceeded);
        Assert.Equal(promotion, _git.FindCommit("main")!.Sha);
        Assert.Equal("work/WP-008", _git.CurrentBranch());
        Assert.Equal("preserve\n", File.ReadAllText(
            System.IO.Path.Combine(_tmp.Root, "external-untracked.txt")));
        Assert.False(_git.IsClean());
    }

    [Fact]
    public void Main_ref_conflict_before_compare_and_swap_is_preserved()
    {
        _git.CreateAndCheckoutBranch("work/WP-009");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "candidate.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        var main = _git.FindCommit("main")!.Sha;
        var promotion = _git.PrepareSquashPromotion(
            "work/WP-009", main, candidate, "WP-009: conflict");
        _git.SquashPromotionFailpoint = phase =>
        {
            if (phase == SquashPromotionPhase.BeforeMainCompareAndSwap)
                _git.UpdateRefCompareAndSwap("refs/heads/main", candidate, main);
        };

        Assert.Throws<GitException>(() =>
            _git.CompleteSquashPromotion("work/WP-009", main, candidate, promotion));

        Assert.Equal(candidate, _git.FindCommit("main")!.Sha);
        Assert.Equal("work/WP-009", _git.CurrentBranch());
        Assert.Equal(candidate, _git.HeadSha());
        Assert.True(_git.IsClean());
    }

    [Fact]
    public void Prepared_objects_remain_recoverable_after_branch_deletion_and_aggressive_gc()
    {
        _git.CreateAndCheckoutBranch("work/WP-010");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "durable.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        var main = _git.FindCommit("main")!.Sha;
        var promotion = _git.PrepareSquashPromotion(
            "work/WP-010", main, candidate, "WP-010: durable recovery");

        Assert.Contains(promotion, RunGit("for-each-ref", "--format=%(objectname)",
            "refs/tenninety/prepared"));
        Assert.Contains(candidate, RunGit("for-each-ref", "--format=%(objectname)",
            "refs/tenninety/candidates"));
        _git.CompleteSquashPromotion("work/WP-010", main, candidate, promotion);
        _git.DeleteBranchCompareAndSwap("work/WP-010", candidate);
        RunGit("reflog", "expire", "--expire=now", "--all");
        RunGit("gc", "--prune=now");

        _git.CompleteSquashPromotion("work/WP-010", main, candidate, promotion);
        _git.ReleaseSquashPromotion(candidate, promotion);

        Assert.Empty(RunGit("for-each-ref", "--format=%(refname)", "refs/tenninety"));
        Assert.Equal(promotion, _git.HeadSha());
        Assert.True(_git.IsClean());
    }

    [Fact]
    public void Publication_refuses_an_unsigned_object_when_signing_becomes_required()
    {
        _git.CreateAndCheckoutBranch("work/WP-011");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "policy.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        var main = _git.FindCommit("main")!.Sha;
        var promotion = _git.PrepareSquashPromotion(
            "work/WP-011", main, candidate, "WP-011: policy transition");
        _git.SetLocalConfig("commit.gpgsign", "true");

        var error = Assert.Throws<InvalidOperationException>(() =>
            _git.CompleteSquashPromotion("work/WP-011", main, candidate, promotion));

        Assert.Contains("signed promotion object", error.Message);
        Assert.Equal(main, _git.FindCommit("main")!.Sha);
        Assert.Equal("work/WP-011", _git.CurrentBranch());
        Assert.Equal(candidate, _git.HeadSha());
        _git.ReleaseSquashPromotion(candidate, promotion);
    }

    [Fact]
    public void Compare_and_swap_deletion_preserves_a_branch_checked_out_in_another_worktree()
    {
        _git.CreateAndCheckoutBranch("work/WP-012");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "linked.txt"), "reviewed\n");
        var candidate = _git.CommitAll("reviewed candidate")!;
        _git.CheckoutBranch("main");
        using var linkedParent = new TempDir();
        var linkedPath = System.IO.Path.Combine(linkedParent.Root, "checkout");
        RunGit("worktree", "add", linkedPath, "work/WP-012");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                _git.DeleteBranchCompareAndSwap("work/WP-012", candidate));

            Assert.Contains("another worktree", error.Message);
            Assert.True(_git.BranchExists("work/WP-012"));
        }
        finally
        {
            RunGit("worktree", "remove", "--force", linkedPath);
        }
    }

    [Fact]
    public void Bounded_diff_returns_a_small_patch_exactly()
    {
        _git.CreateAndCheckoutBranch("work/small-diff");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "small.txt"), "small change\n");
        _git.CommitAll("small diff");

        var expected = RunGitRaw("diff", "main...work/small-diff");
        var actual = _git.DiffPatchAgainstMain("work/small-diff", 10_000);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("[diff truncated", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void Bounded_diff_retains_only_the_configured_head_and_tail_of_a_large_patch()
    {
        const int maxChars = 1000;
        const string marker = "\n… [diff truncated – showing head and tail] …\n";
        _git.CreateAndCheckoutBranch("work/large-diff");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "000-head.txt"),
            "HEAD-SENTINEL\n" + new string('H', 250_000) + "\n");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "zzz-tail.txt"),
            new string('T', 250_000) + "\nTAIL-SENTINEL\n");
        _git.CommitAll("large diff");

        var full = RunGitRaw("diff", "main...work/large-diff");
        var actual = _git.DiffPatchAgainstMain("work/large-diff", maxChars);

        Assert.True(full.Length > maxChars);
        Assert.Equal(full[..750], actual[..750]);
        Assert.EndsWith(full[^250..], actual, StringComparison.Ordinal);
        Assert.Contains("HEAD-SENTINEL", actual, StringComparison.Ordinal);
        Assert.Contains("TAIL-SENTINEL", actual, StringComparison.Ordinal);
        Assert.Equal(maxChars + marker.Length, actual.Length);
        Assert.Equal(1, actual.Split(marker, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Bounded_diff_decodes_valid_utf8_without_broken_surrogates_at_elision_boundaries()
    {
        _git.CreateAndCheckoutBranch("work/unicode-diff");
        File.WriteAllText(System.IO.Path.Combine(_tmp.Root, "unicode.txt"),
            "UNICODE-START\n" + string.Concat(Enumerable.Repeat("🙂漢", 2000)) +
            "\nUNICODE-TAIL-🙂-漢\n");
        _git.CommitAll("unicode diff");

        var patch = _git.DiffPatchAgainstMain("work/unicode-diff", 257);

        Assert.Contains("[diff truncated", patch, StringComparison.Ordinal);
        Assert.Contains("UNICODE-TAIL-🙂-漢", patch, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFFFD', patch);
        for (var i = 0; i < patch.Length; i++)
        {
            if (char.IsHighSurrogate(patch[i]))
            {
                Assert.True(i + 1 < patch.Length && char.IsLowSurrogate(patch[i + 1]));
                i++;
            }
            else
                Assert.False(char.IsLowSurrogate(patch[i]));
        }
    }

    [Fact]
    public void Bounded_diff_preserves_git_failures()
    {
        var ex = Assert.Throws<GitException>(() =>
            _git.DiffPatchAgainstMain("branch-that-does-not-exist", 1000));

        Assert.Contains("git 'diff main...branch-that-does-not-exist' failed", ex.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Bounded_diff_rejects_non_positive_limits_before_running_git(int maxChars) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _git.DiffPatchAgainstMain("work/anything", maxChars));

    private string RunGit(params string[] args)
        => RunGitRaw(args).Trim();

    private string RunGitRaw(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _tmp.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return stdout;
    }

    public void Dispose() => _tmp.Dispose();
}
