using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Tests for the trusted host path value objects under the project's invariant-globalization
/// runtime: normalization APIs are silent no-ops there, so the v1 policy accepts PRINTABLE
/// ASCII ONLY for trusted host paths and rejects every non-ASCII path BEFORE any filesystem
/// access — proven here against REAL existing decomposed and precomposed directories.
/// Also covers the managed-root writability requirement (no group/other write bits).
/// </summary>
public class ValidatedSandboxWorkspacePathTests : IDisposable
{
    private readonly TempDir _asciiParent = new();
    private readonly TestGitRepo _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
        _asciiParent.Dispose();
    }

    [Fact]
    public void Existing_decomposed_unicode_managed_root_is_rejected_before_any_attempt()
    {
        // A REAL existing directory whose name contains decomposed Unicode (e + combining
        // acute), beneath an ASCII parent.
        var decomposed = Directory.CreateDirectory(
            Path.Combine(_asciiParent.Root, "cafe\u0301-root")).FullName;
        Assert.True(Directory.Exists(decomposed), "fixture must pre-exist");

        Assert.Throws<InvalidOperationException>(() =>
            ValidatedManagedRootPath.Create(decomposed));

        // A factory run against this root creates nothing anywhere.
        _repo.WriteFile("a.txt", "content");
        var sha = _repo.Commit("non-ascii root fixture");
        Assert.Throws<InvalidOperationException>(() =>
            new CandidateWorkspaceFactory(_repo.Git)
                .Create(new CandidateWorkspaceRequest
                {
                    CommitSha = sha,
                    ManagedRoot = decomposed,
                }));
        Assert.Empty(Directory.EnumerateFileSystemEntries(decomposed));
    }

    [Fact]
    public void Existing_precomposed_unicode_managed_root_is_rejected()
    {
        // The chosen v1 policy is ASCII-only: even a precomposed non-ASCII name is rejected.
        var precomposed = Directory.CreateDirectory(
            Path.Combine(_asciiParent.Root, "caf\u00e9-root")).FullName;
        Assert.True(Directory.Exists(precomposed), "fixture must pre-exist");

        Assert.Throws<InvalidOperationException>(() =>
            ValidatedManagedRootPath.Create(precomposed));
        Assert.Empty(Directory.EnumerateFileSystemEntries(precomposed));
    }

    [Fact]
    public void Existing_non_ascii_workspace_candidate_component_is_rejected()
    {
        var managedRoot = Directory.CreateDirectory(
            Path.Combine(_asciiParent.Root, "managed")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(managedRoot, "sourc\u00e9")).FullName; // precomposed non-ASCII
        Assert.True(Directory.Exists(source), "fixture must pre-exist");

        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(source, managedRoot, _repo.Root));
        // Nothing was created beneath the managed root by the validation attempt.
        Assert.Single(Directory.EnumerateFileSystemEntries(managedRoot)); // only "source"
    }

    [Fact]
    public void Existing_non_ascii_repository_path_component_is_rejected()
    {
        var managedRoot = Directory.CreateDirectory(
            Path.Combine(_asciiParent.Root, "managed")).FullName;
        var source = Directory.CreateDirectory(
            Path.Combine(managedRoot, "workspace")).FullName;
        var nonAsciiRepo = Directory.CreateDirectory(
            Path.Combine(_asciiParent.Root, "r\u00e9pository")).FullName; // precomposed non-ASCII
        Assert.True(Directory.Exists(nonAsciiRepo), "fixture must pre-exist");

        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(source, managedRoot, nonAsciiRepo));
        Assert.Single(Directory.EnumerateFileSystemEntries(managedRoot)); // only "source"
    }

    [Fact]
    public void Group_or_world_writable_managed_root_is_rejected()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(_asciiParent.Root, "writable-root")).FullName;
        var originalMode = File.GetUnixFileMode(root);
        try
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var ex = Assert.Throws<InvalidOperationException>(
                () => ValidatedManagedRootPath.Create(root));
            Assert.Contains("group-writable or world-writable", ex.Message);
        }
        finally
        {
            // Restore so the temp directory remains removable.
            File.SetUnixFileMode(root, originalMode);
        }
    }
}

/// <summary>
/// Security-verification tests for the disposable Git execution profile's support
/// directories: they must be real, non-symlinked, EMPTY directories with owner-only
/// permissions (no group/other write, read or execute bits), and disposable repository
/// initialization must actually use the empty trusted template.
/// </summary>
public class GitServiceDisposableEnvironmentTests
{
    [Fact]
    public void Disposable_support_directories_are_owner_only_real_and_empty()
    {
        foreach (var directory in new[]
                 {
                     GitService.DisposableHomeDirectory,
                     GitService.DisposableTemplateDirectory,
                 })
        {
            var info = new DirectoryInfo(directory);
            Assert.True(info.Exists, "the support directory must exist");
            Assert.Null(info.LinkTarget); // a real directory, never a symlink/reparse point

            var mode = File.GetUnixFileMode(directory);
            Assert.False(mode.HasFlag(UnixFileMode.GroupWrite), "no group write");
            Assert.False(mode.HasFlag(UnixFileMode.OtherWrite), "no other write");
            Assert.False(mode.HasFlag(UnixFileMode.GroupRead), "no group read");
            Assert.False(mode.HasFlag(UnixFileMode.OtherRead), "no other read");
            Assert.False(mode.HasFlag(UnixFileMode.GroupExecute), "no group execute");
            Assert.False(mode.HasFlag(UnixFileMode.OtherExecute), "no other execute");
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
            Assert.True(mode.HasFlag(UnixFileMode.UserWrite));
            Assert.True(mode.HasFlag(UnixFileMode.UserRead));

            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
    }

    [Fact]
    public void Disposable_initialization_uses_the_empty_trusted_template()
    {
        var repoDir = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"tenninety-disposable-{Guid.NewGuid():N}"));
        try
        {
            var git = GitService.CreateDisposable(repoDir.FullName);
            git.Init();

            // The empty template means NO hook files (not even samples) were created.
            var hooksDir = Path.Combine(repoDir.FullName, ".git", "hooks");
            Assert.False(Directory.Exists(hooksDir) &&
                         Directory.EnumerateFileSystemEntries(hooksDir).Any());

            // The fresh repository has a local identity (no global config is reachable) and
            // knows no remotes.
            Assert.Equal("tenninety", TestGitRepo.RunGitIn(repoDir.FullName, "config", "user.name").Trim());
            Assert.Equal("", TestGitRepo.RunGitIn(repoDir.FullName, "remote").Trim());
        }
        finally
        {
            repoDir.Delete(recursive: true);
        }
    }
}
