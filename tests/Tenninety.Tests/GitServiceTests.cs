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

    private string RunGit(params string[] args)
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
        return stdout.Trim();
    }

    public void Dispose() => _tmp.Dispose();
}
