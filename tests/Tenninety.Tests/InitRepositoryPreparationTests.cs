using Tenninety.Cli.Commands;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// tenninety init must be predictable in EXISTING Git repositories: a repository on any branch
/// other than main is refused with exact remediation (never renamed or switched), a missing
/// commit identity is resolved with the repository-local Tenninety identity, subdirectory and
/// bare-repository invocations are refused before any file is written, and worktrees are
/// detected exactly like Git sees them.
/// </summary>
public sealed class InitRepositoryPreparationTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly GitService _git;

    public InitRepositoryPreparationTests()
    {
        _git = new GitService(_tmp.Root);
    }

    public void Dispose() => _tmp.Dispose();

    private void CommitInitial()
    {
        File.WriteAllText(_tmp.Path("README.md"), "demo\n");
        _git.CommitAll("initial");
    }

    [Fact]
    public void A_directory_without_a_repository_is_reported_as_fresh()
    {
        var check = InitCommand.PrepareExistingRepository(_tmp.Root);

        Assert.False(check.RepositoryPresent);
        Assert.Null(check.Error);
    }

    [Fact]
    public void An_existing_repository_on_main_is_accepted()
    {
        _git.Init();
        CommitInitial();
        Assert.Equal("main", _git.CurrentBranch());

        var check = InitCommand.PrepareExistingRepository(_tmp.Root);

        Assert.True(check.RepositoryPresent);
        Assert.Null(check.Error);
    }

    [Fact]
    public void An_existing_repository_on_master_is_refused_with_exact_remediation()
    {
        _git.Init();
        // Simulate a legacy default branch without renaming anything afterwards.
        _git.RunForTest("symbolic-ref", "HEAD", "refs/heads/master");
        CommitInitial();
        Assert.Equal("master", _git.CurrentBranch());

        var check = InitCommand.PrepareExistingRepository(_tmp.Root);

        Assert.True(check.RepositoryPresent);
        Assert.NotNull(check.Error);
        Assert.Contains("'master'", check.Error);
        Assert.Contains("main", check.Error);
        Assert.Contains("git branch -m master main", check.Error);
        // Non-destructive: the branch is untouched.
        Assert.Equal("master", _git.CurrentBranch());
    }

    [Fact]
    public void A_detached_head_is_refused_with_exact_remediation()
    {
        _git.Init();
        CommitInitial();
        File.WriteAllText(_tmp.Path("b.txt"), "second\n");
        _git.CommitAll("second");
        _git.RunForTest("checkout", "--detach", "HEAD~1");

        var check = InitCommand.PrepareExistingRepository(_tmp.Root);

        Assert.NotNull(check.Error);
        Assert.Contains("detached-HEAD", check.Error);
        Assert.Contains("git switch -c main", check.Error);
    }

    [Fact]
    public void A_missing_identity_is_resolved_with_the_repository_local_tenninety_identity()
    {
        _git.Init();
        // Make the commit identity unresolvable the same way a fresh operator machine looks:
        // empty HOME/XDG config hides any global Git identity from every git child process.
        var emptyHome = Directory.CreateDirectory(_tmp.Path("empty-home")).FullName;
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        var previousXdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        Environment.SetEnvironmentVariable("HOME", emptyHome);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", emptyHome);
        try
        {
            // A repository that has never been committed to and has no identity anywhere.
            Assert.False(HasLocalIdentity());

            var check = InitCommand.PrepareExistingRepository(_tmp.Root);

            Assert.True(check.RepositoryPresent);
            Assert.Null(check.Error);
            Assert.True(HasLocalIdentity());
            Assert.Equal("tenninety", _git.ShowLocalConfigForTest("user.name"));
            Assert.Equal("tenninety@localhost", _git.ShowLocalConfigForTest("user.email"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", previousHome);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousXdg);
        }
    }

    [Fact]
    public void An_existing_operator_identity_is_never_overwritten()
    {
        _git.Init();
        _git.SetLocalConfig("user.name", "Operator");
        _git.SetLocalConfig("user.email", "operator@example.test");

        var check = InitCommand.PrepareExistingRepository(_tmp.Root);

        Assert.Null(check.Error);
        Assert.Equal("Operator", _git.ShowLocalConfigForTest("user.name"));
        Assert.Equal("operator@example.test", _git.ShowLocalConfigForTest("user.email"));
    }

    [Fact]
    public void Invoking_from_a_subdirectory_is_refused_and_creates_no_nested_repository()
    {
        _git.Init();
        CommitInitial();
        var subdir = _tmp.Path("src");
        Directory.CreateDirectory(subdir);

        var check = InitCommand.PrepareExistingRepository(subdir);

        Assert.NotNull(check.Error);
        Assert.Contains("top level", check.Error);
        Assert.Contains("nested repository", check.Error);
        // Nothing was written into the subdirectory.
        Assert.Empty(Directory.GetFileSystemEntries(subdir));
        Assert.False(new GitService(subdir).IsRepository());
    }

    [Fact]
    public void A_linked_worktree_on_a_side_branch_is_refused()
    {
        _git.Init();
        CommitInitial();
        var worktreePath = _tmp.Path("wt");
        _git.RunForTest("worktree", "add", "--detach", worktreePath, "HEAD");

        var check = InitCommand.PrepareExistingRepository(worktreePath);

        Assert.NotNull(check.Error);
        Assert.Contains("detached-HEAD", check.Error);
        // The worktree itself is untouched.
        Assert.True(Directory.Exists(worktreePath));
    }

    [Fact]
    public void A_bare_repository_is_refused()
    {
        var bare = _tmp.Path("bare.git");
        Directory.CreateDirectory(bare);
        new GitService(bare).RunForTest("init", "--bare");

        var check = InitCommand.PrepareExistingRepository(bare);

        Assert.NotNull(check.Error);
        Assert.Contains("bare", check.Error);
    }

    [Fact]
    public void GitService_top_level_detection_finds_the_enclosing_repository()
    {
        _git.Init();
        CommitInitial();
        var subdir = Directory.CreateDirectory(_tmp.Path("src/feature")).FullName;

        var git = new GitService(subdir);
        Assert.NotNull(git.RepositoryTopLevel());
        Assert.Equal(_tmp.Root.TrimEnd('/'), Path.GetFullPath(git.RepositoryTopLevel()!).TrimEnd('/'));
    }

    private bool HasLocalIdentity() =>
        _git.ShowLocalConfigForTest("user.name") is not null &&
        _git.ShowLocalConfigForTest("user.email") is not null;
}
