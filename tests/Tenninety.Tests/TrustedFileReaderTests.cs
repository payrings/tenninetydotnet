using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Direct tests for the Linux no-follow regular-file reader and its integration into the
/// extraction scan: every symlink kind (file→regular file, directory, broken) is rejected,
/// and post-open mode/content changes are deterministically detected by the captured
/// metadata comparison.
/// </summary>
public class TrustedFileReaderTests : IDisposable
{
    private readonly TestGitRepo _repo = new();
    private readonly TempDir _managedRoot = new();
    private readonly QuiescenceProof _proof;

    private CandidateWorkspace Workspace { get; }
    private string CandidateSha { get; }

    public TrustedFileReaderTests()
    {
        _repo.WriteFile("sentinel.txt", "sentinel\n");
        _repo.Commit("initial on main");
        _repo.Git.CreateAndCheckoutBranch("work/WP-001");
        _repo.WriteFile("src/existing.txt", "original\n");
        CandidateSha = _repo.Commit("candidate");
        Workspace = new CandidateWorkspaceFactory(_repo.Git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = CandidateSha,
                ManagedRoot = _managedRoot.Root,
                WorkBranch = "work/WP-001",
                MainBaseSha = _repo.Git.HeadSha(),
                Role = SandboxRole.Coder,
                RunId = "run-1",
                AttemptId = "attempt-1",
            });
        _proof = QuiescenceProof.Issue(
            "run-1", "attempt-1", SandboxRole.Coder, Workspace.AttemptRoot,
            "test harness: container stopped");
    }

    public void Dispose()
    {
        _managedRoot.Dispose();
        _repo.Dispose();
    }

    private CandidateScanResult Scan() =>
        new CandidateScanner(_repo.Git).Scan(Workspace, _proof);

    [Fact]
    public void Trusted_reader_rejects_file_symlink_to_existing_regular_file()
    {
        var target = Path.Combine(Workspace.SourcePath, "src/existing.txt");
        Assert.True(File.Exists(target)); // the symlink points at an existing regular file
        var link = Path.Combine(Workspace.SourcePath, "src/link.txt");
        File.CreateSymbolicLink(link, target);

        var ex = Assert.Throws<InvalidOperationException>(
            () => TrustedFileReader.OpenRegularFileNoFollow(link));
        Assert.Contains("symlink", ex.Message);
    }

    [Fact]
    public void Trusted_reader_rejects_mode_change_after_open()
    {
        var path = Path.Combine(Workspace.SourcePath, "src/existing.txt");
        using var opened = TrustedFileReader.OpenRegularFileNoFollow(path);

        // Deterministically change the MODE after open (chmod): ctime changes, size does not.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        Assert.Throws<InvalidOperationException>(() => opened.VerifyUnchanged(opened.Length));
    }

    [Fact]
    public void Trusted_reader_rejects_same_length_content_change_after_open()
    {
        var path = Path.Combine(Workspace.SourcePath, "src/existing.txt");
        using var opened = TrustedFileReader.OpenRegularFileNoFollow(path);

        // Deterministically rewrite the file in place with SAME-LENGTH different content:
        // size is unchanged but mtime/ctime change, so the metadata comparison must fail.
        var original = File.ReadAllText(path);
        var flipped = new string(original.Select(c => c == 'o' ? 'O' : c).ToArray());
        Assert.Equal(original.Length, flipped.Length);
        Assert.NotEqual(original, flipped);
        File.WriteAllText(path, flipped);

        Assert.Throws<InvalidOperationException>(() => opened.VerifyUnchanged(opened.Length));
    }

    [Fact]
    public void Trusted_reader_accepts_unchanged_regular_file()
    {
        using var opened = TrustedFileReader.OpenRegularFileNoFollow(
            Path.Combine(Workspace.SourcePath, "src/existing.txt"));
        Assert.Equal("original\n".Length, opened.Length);
        Assert.False(opened.Executable);
        opened.VerifyUnchanged(opened.Length); // no exception: unchanged
    }

    [Fact]
    public void Scanner_rejects_file_symlink_to_existing_regular_file()
    {
        File.CreateSymbolicLink(
            Path.Combine(Workspace.SourcePath, "src/indirect.txt"),
            Path.Combine(Workspace.SourcePath, "src/existing.txt"));
        Assert.Throws<InvalidOperationException>(() => Scan());
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Scanner_rejects_directory_symlink()
    {
        Directory.CreateSymbolicLink(
            Path.Combine(Workspace.SourcePath, "src/linkdir"),
            Path.Combine(Workspace.SourcePath, "src"));
        Assert.Throws<InvalidOperationException>(() => Scan());
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Scanner_rejects_broken_symlink()
    {
        File.CreateSymbolicLink(
            Path.Combine(Workspace.SourcePath, "src/broken.txt"),
            "no-such-target-anywhere");
        Assert.Throws<InvalidOperationException>(() => Scan());
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
    }
}
