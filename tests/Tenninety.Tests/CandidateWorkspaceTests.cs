using System.Text;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Tenninety.Tests;

/// <summary>Disposable real-git repository fixture for candidate materialization tests.
/// Also supports hand-crafted trees (hash-object/commit-tree) so hostile tree entries that
/// git itself would never produce (traversal, absolute, case-colliding, unsupported modes)
/// can be tested exactly the way a malicious or corrupted tree would reach the parser.</summary>
internal sealed class TestGitRepo : IDisposable
{
    public TempDir Dir { get; } = new();
    public GitService Git { get; }
    public string Root => Dir.Root;

    public TestGitRepo()
    {
        Git = new GitService(Root);
        Git.Init();
        // Hermetic identity: the fixture must not depend on the operator's global git
        // identity (which hostile-config tests deliberately hide).
        Git.SetLocalConfig("user.name", "tenninety");
        Git.SetLocalConfig("user.email", "tenninety@localhost");
    }

    public void WriteFile(string relativePath, string content) =>
        WriteFile(relativePath, Encoding.UTF8.GetBytes(content));

    public void WriteFile(string relativePath, byte[] bytes)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void MakeExecutable(string relativePath)
    {
        var path = Path.Combine(Root, relativePath);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    public string Commit(string message)
    {
        Git.StageAll();
        return Git.CommitStaged(message)
            ?? throw new InvalidOperationException("test fixture had nothing to commit");
    }

    public string HashObject(byte[] content)
    {
        var tmp = Path.GetTempFileName();
        File.WriteAllBytes(tmp, content);
        try
        {
            // --no-filters: the fixture must learn the RAW content oid regardless of any
            // configured clean/smudge/eol filter or autocrlf setting.
            return Run("hash-object", "-w", "--no-filters", tmp).Trim();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Writes a RAW loose tree object whose entries are not validated by git in any
    /// way (modern git's hash-object refuses to fsck-violating trees), exactly like a
    /// hand-crafted or corrupted tree would look to the parser. Returns the object id.</summary>
    public string WriteRawTreeObject(params (string Mode, string Name, string Sha)[] entries)
    {
        using var payload = new MemoryStream();
        foreach (var (mode, name, sha) in entries)
        {
            payload.Write(Encoding.ASCII.GetBytes($"{mode} {name}"));
            payload.WriteByte(0);
            payload.Write(HexToBytes(sha));
        }
        var body = payload.ToArray();
        var store = new byte[Encoding.ASCII.GetByteCount($"tree {body.Length}\0") + body.Length];
        var headerLength = Encoding.ASCII.GetBytes($"tree {body.Length}\0", store);
        body.CopyTo(store, headerLength);

        // Loose object format: "tree <len>\0<payload>", zlib-deflated, under sha1/<38 hex>.
        var objectSha = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(store))
            .ToLowerInvariant();
        var objectPath = Path.Combine(Root, ".git", "objects", objectSha[..2], objectSha[2..]);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        using (var raw = File.Create(objectPath))
        using (var zlib = new System.IO.Compression.ZLibStream(raw, System.IO.Compression.CompressionLevel.Optimal))
        {
            zlib.Write(store, 0, store.Length);
        }
        return objectSha;
    }

    public string CommitTree(string treeSha, string message) =>
        Run("commit-tree", treeSha, "-m", message).Trim();

    public string Run(params string[] args) => RunGitIn(Root, args);

    /// <summary>Runs git under the disposable execution CONTRACT (empty home, no global or
    /// system config, no prompting) — used by tests to observe what Tenninety's disposable
    /// git invocations can and cannot see, independently of the implementation.</summary>
    internal static string RunGitInIsolatedEnv(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment.Clear();
        psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin";
        psi.Environment["HOME"] = Path.Combine(Path.GetTempPath(), "tenninety-test-empty-home");
        Directory.CreateDirectory(psi.Environment["HOME"]);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start git in test fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"test fixture git '{string.Join(' ', args)}' failed: {stderr.GetAwaiter().GetResult().Trim()}");
        return stdout.GetAwaiter().GetResult();
    }

    internal static string RunGitIn(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start git in test fixture.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"test fixture git '{string.Join(' ', args)}' failed: {error.Trim()}");
        return stdout.GetAwaiter().GetResult();
    }

    private static byte[] HexToBytes(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    public void Dispose() => Dir.Dispose();
}

/// <summary>
/// End-to-end Phase 2 tests: exact candidate materialization into a disposable, validated,
/// verified workspace with an untrusted one-commit agent `.git`. Test parallelization is
/// disabled because the hostile-global-config tests temporarily mutate process environment
/// variables (always restored in finally blocks).
/// </summary>
public class CandidateWorkspaceTests : IDisposable
{
    private readonly TestGitRepo _repo = new();
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _outside = new();

    public void Dispose()
    {
        _outside.Dispose();
        _managedRoot.Dispose();
        _repo.Dispose();
    }

    private CandidateWorkspaceFactory Factory => new(_repo.Git);

    private CandidateWorkspaceRequest Request(
        string sha, MaterializationLimits? limits = null, string? managedRoot = null) => new()
    {
        CommitSha = sha,
        ManagedRoot = managedRoot ?? _managedRoot.Root,
        WorkBranch = "work/WP-001",
        MainBaseSha = new string('0', 40),
        Role = SandboxRole.Coder,
        RunId = "run-1",
        AttemptId = "attempt-1",
        Limits = limits,
    };

    [Fact]
    public void Materializes_exact_regular_file_content()
    {
        _repo.WriteFile("src/a.txt", "hello candidate");
        _repo.WriteFile("src/deep/b.txt", "nested");
        var sha = _repo.Commit("candidate");

        var workspace = Factory.Create(Request(sha));

        Assert.Equal("hello candidate", File.ReadAllText(Path.Combine(workspace.SourcePath, "src/a.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(workspace.SourcePath, "src/deep/b.txt")));
        Assert.Equal(sha, workspace.Revision.CommitSha);
        Assert.Equal("work/WP-001", workspace.Revision.WorkBranch);
        Assert.Equal(_repo.Git.ResolveTreeOfCommit(sha), workspace.BaselineTreeOid);
        Assert.Equal(SandboxRole.Coder, workspace.Role);
    }

    [Fact]
    public void Binary_content_survives_byte_for_byte()
    {
        var bytes = Enumerable.Range(0, 512).Select(i => (byte)(i % 256)).ToArray();
        _repo.WriteFile("data/blob.bin", bytes);
        var sha = _repo.Commit("binary candidate");

        var workspace = Factory.Create(Request(sha));

        Assert.True(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(workspace.SourcePath, "data/blob.bin"))));
    }

    [Fact]
    public void Executable_mode_survives()
    {
        _repo.WriteFile("run.sh", "#!/bin/sh\necho hi\n");
        _repo.MakeExecutable("run.sh");
        var sha = _repo.Commit("executable candidate");

        var workspace = Factory.Create(Request(sha));

        var materialized = Path.Combine(workspace.SourcePath, "run.sh");
        var mode = File.GetUnixFileMode(materialized);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        Assert.True(mode.HasFlag(UnixFileMode.GroupExecute));
        Assert.True(mode.HasFlag(UnixFileMode.OtherExecute));
        Assert.True(mode.HasFlag(UnixFileMode.UserRead));
    }

    [Fact]
    public void Candidate_is_selected_by_sha_not_working_tree_state()
    {
        _repo.WriteFile("a.txt", "version one");
        var first = _repo.Commit("first candidate");
        _repo.WriteFile("b.txt", "only in head");
        var head = _repo.Commit("second candidate");

        // Dirty the working tree after the fact: uncommitted edit + untracked file.
        _repo.WriteFile("a.txt", "uncommitted working-tree edit");
        _repo.WriteFile("untracked.txt", "never committed");

        var workspace = Factory.Create(Request(first));

        Assert.Equal("version one", File.ReadAllText(Path.Combine(workspace.SourcePath, "a.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.SourcePath, "b.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.SourcePath, "untracked.txt")));
        Assert.NotEqual(head, workspace.Revision.CommitSha);
    }

    [Fact]
    public void Untracked_and_ignored_files_are_absent()
    {
        _repo.WriteFile(".gitignore", "ignored.txt\n");
        _repo.WriteFile("tracked.txt", "tracked");
        var sha = _repo.Commit("with ignore rules");
        _repo.WriteFile("ignored.txt", "ignored content");
        _repo.WriteFile("untracked.txt", "untracked content");

        var workspace = Factory.Create(Request(sha));

        Assert.True(File.Exists(Path.Combine(workspace.SourcePath, "tracked.txt")));
        Assert.True(File.Exists(Path.Combine(workspace.SourcePath, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(workspace.SourcePath, "ignored.txt")));
        Assert.False(File.Exists(Path.Combine(workspace.SourcePath, "untracked.txt")));
    }

    [Fact]
    public void Agent_git_has_one_baseline_commit_and_no_authoritative_history()
    {
        _repo.WriteFile("first.txt", "one");
        var first = _repo.Commit("first");
        _repo.WriteFile("second.txt", "two");
        var head = _repo.Commit("second");

        var workspace = Factory.Create(Request(head));
        var source = workspace.SourcePath;

        // Exactly one baseline commit…
        Assert.Equal("1", TestGitRepo.RunGitIn(source, "rev-list", "--count", "HEAD").Trim());
        // …whose object store knows nothing of the authoritative history…
        Assert.Null(new GitService(source).FindCommit(first));
        // …with no remote and no alternates.
        Assert.Equal("", TestGitRepo.RunGitIn(source, "remote").Trim());
        Assert.False(File.Exists(Path.Combine(source, ".git", "objects", "info", "alternates")));
        // Hooks are disabled for the agent tooling repository.
        Assert.Equal("/dev/null", TestGitRepo.RunGitIn(source, "config", "core.hooksPath").Trim());
    }

    [Fact]
    public void Symlink_entries_are_rejected()
    {
        _repo.WriteFile("target.txt", "target");
        var oid = _repo.HashObject(Encoding.UTF8.GetBytes("target.txt"));
        var tree = _repo.WriteRawTreeObject(
            ("100644", "target.txt", oid),
            ("120000", "link", oid));
        var sha = _repo.CommitTree(tree, "hostile symlink entry");

        var ex = Assert.Throws<InvalidOperationException>(() => Factory.Create(Request(sha)));
        Assert.Contains("120000", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Gitlink_entries_are_rejected()
    {
        var tree = _repo.WriteRawTreeObject(
            ("100644", "file.txt", _repo.HashObject(Encoding.UTF8.GetBytes("content"))),
            ("160000", "submodule", new string('d', 40)));
        var sha = _repo.CommitTree(tree, "hostile gitlink entry");

        var ex = Assert.Throws<InvalidOperationException>(() => Factory.Create(Request(sha)));
        Assert.Contains("160000", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Parent_traversal_paths_are_rejected()
    {
        var oid = _repo.HashObject(Encoding.UTF8.GetBytes("evil"));
        var tree = _repo.WriteRawTreeObject(("100644", "../evil", oid));
        var sha = _repo.CommitTree(tree, "hostile traversal entry");

        Assert.Throws<InvalidOperationException>(() => Factory.Create(Request(sha)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Absolute_paths_are_rejected()
    {
        var oid = _repo.HashObject(Encoding.UTF8.GetBytes("evil"));
        var tree = _repo.WriteRawTreeObject(("100644", "/etc/evil", oid));
        var sha = _repo.CommitTree(tree, "hostile absolute entry");

        Assert.Throws<InvalidOperationException>(() => Factory.Create(Request(sha)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Case_colliding_paths_are_rejected()
    {
        _repo.WriteFile("a.txt", "lower");
        _repo.WriteFile("A.txt", "upper");
        var sha = _repo.Commit("case-colliding candidate");

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha));
        });
        Assert.Contains("colliding", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Exact_duplicate_paths_are_rejected()
    {
        var oid = _repo.HashObject(Encoding.UTF8.GetBytes("dup"));
        var tree = _repo.WriteRawTreeObject(
            ("100644", "dup.txt", oid),
            ("100644", "dup.txt", oid));
        var sha = _repo.CommitTree(tree, "duplicate entry candidate");

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha));
        });
        Assert.Contains("colliding", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Unusual_delimiter_and_backslash_names_fail_closed()
    {
        _repo.WriteFile("we\tird.txt", "tab in name");
        var sha = _repo.Commit("tab-named candidate");
        var ex = Assert.Throws<InvalidOperationException>(() => Factory.Create(Request(sha)));
        Assert.Contains("safe repository-relative path", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));

        _repo.WriteFile("back\\slash.txt", "backslash in name");
        var sha2 = _repo.Commit("backslash-named candidate");
        Assert.Throws<InvalidOperationException>(() => Factory.Create(Request(sha2)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void File_count_limit_fails_closed_before_copy()
    {
        for (var i = 0; i < 3; i++) _repo.WriteFile($"f{i}.txt", "content");
        var sha = _repo.Commit("three files");

        Assert.Throws<InvalidOperationException>(
            () => Factory.Create(Request(sha, new MaterializationLimits { MaxFiles = 2 })));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Byte_limit_fails_closed()
    {
        _repo.WriteFile("big.bin", new byte[512]);
        var sha = _repo.Commit("one blob over budget");

        Assert.Throws<InvalidOperationException>(
            () => Factory.Create(Request(sha, new MaterializationLimits { MaxTotalBytes = 10 })));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Attempt_paths_are_strict_children_of_the_managed_root()
    {
        _repo.WriteFile("a.txt", "content");
        var sha = _repo.Commit("containment candidate");

        var workspace = Factory.Create(Request(sha));

        var root = _managedRoot.Root;
        Assert.StartsWith(root + "/", workspace.AttemptRoot, StringComparison.Ordinal);
        Assert.StartsWith(root + "/", workspace.SourcePath, StringComparison.Ordinal);
        Assert.StartsWith(root + "/", workspace.TrustedIngestionPath, StringComparison.Ordinal);
        Assert.NotEqual(root, workspace.AttemptRoot);
        Assert.NotEqual(root, workspace.SourcePath);
        Assert.Equal(Path.Combine(workspace.AttemptRoot, "source"), workspace.SourcePath);
        Assert.Equal(Path.Combine(workspace.AttemptRoot, "ingestion"), workspace.TrustedIngestionPath);
        Assert.True(Directory.Exists(workspace.TrustedIngestionPath));
    }

    [Fact]
    public void Empty_tree_candidate_materializes()
    {
        TestGitRepo.RunGitIn(_repo.Root, "commit", "--allow-empty", "-m", "empty candidate");
        var sha = _repo.Git.HeadSha();

        var workspace = Factory.Create(Request(sha));

        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.SourcePath)
            .Where(e => Path.GetFileName(e) != ".git"));
        // The agent repository still carries exactly one (empty) baseline commit.
        Assert.Equal("1", TestGitRepo.RunGitIn(workspace.SourcePath, "rev-list", "--count", "HEAD").Trim());
        Assert.Equal(_repo.Git.ResolveTreeOfCommit(sha), workspace.BaselineTreeOid);
    }

    [Fact]
    public void Invalid_commit_shas_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => Factory.Create(Request("short-sha")));
        Assert.Throws<InvalidOperationException>(() => Factory.Create(Request(new string('z', 40))));
        Assert.Throws<Tenninety.Git.GitException>(
            () => Factory.Create(Request(new string('a', 40))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Verification_covers_the_ingestion_index()
    {
        _repo.WriteFile("a.txt", "content");
        var sha = _repo.Commit("ingestion candidate");

        var workspace = Factory.Create(Request(sha));

        // The trusted ingestion repository reproduces the candidate tree from the copies:
        // its staged index (populated with filter-free cache-info entries during
        // verification) still writes exactly the candidate tree OID.
        var ingestion = new GitService(workspace.TrustedIngestionPath);
        Assert.True(ingestion.IsRepository());
        Assert.Equal(workspace.BaselineTreeOid, ingestion.WriteTree());
    }

    // ---- Blocker 1: disposable git isolation -------------------------------------

    [Fact]
    public void Hostile_global_git_config_cannot_reach_disposable_repositories()
    {
        var hostileHome = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"tenninety-hostile-{Guid.NewGuid():N}"));
        var hostileTemplate = Directory.CreateDirectory(
            Path.Combine(hostileHome.FullName, "hostile-template"));
        File.WriteAllText(Path.Combine(hostileTemplate.FullName, "marker.txt"), "template marker");
        var filterMarker = Path.Combine(hostileHome.FullName, "filter-marker.txt");
        var configPath = Path.Combine(hostileHome.FullName, ".gitconfig");
        File.WriteAllText(configPath,
            "[remote \"hostile\"]\n" +
            "\turl = https://evil.example/repo.git\n" +
            "\tfetch = +refs/heads/*:refs/remotes/hostile/*\n" +
            "[init]\n" +
            $"\ttemplatedir = {hostileTemplate.FullName}\n" +
            "[filter \"hostilefilter\"]\n" +
            $"\tclean = touch {filterMarker}\n" +
            $"\tsmudge = touch {filterMarker}\n" +
            $"\tprocess = touch {filterMarker}\n" +
            "[core]\n" +
            "\tautocrlf = true\n");

        var saved = new Dictionary<string, string?>();
        foreach (var key in new[]
                 {
                     "HOME", "XDG_CONFIG_HOME", "GIT_CONFIG_GLOBAL",
                     "GIT_CONFIG_SYSTEM", "GIT_CONFIG_NOSYSTEM",
                 })
        {
            saved[key] = Environment.GetEnvironmentVariable(key);
        }
        try
        {
            Environment.SetEnvironmentVariable("HOME", hostileHome.FullName);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", hostileHome.FullName);
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", configPath);
            Environment.SetEnvironmentVariable("GIT_CONFIG_SYSTEM", configPath);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", null);

            // Candidate: .gitattributes wiring the hostile filter for text files plus a
            // normalization-sensitive CRLF text file. The fixture blob keeps its raw CRLF
            // bytes (hash-object --no-filters, raw tree object, no filter-aware add).
            var textOid = _repo.HashObject(Encoding.UTF8.GetBytes("line one\r\nline two\r\n"));
            var attributesOid = _repo.HashObject(Encoding.UTF8.GetBytes(
                "*.txt filter=hostilefilter\n*.txt text eol=crlf\n"));
            var tree = _repo.WriteRawTreeObject(
                ("100644", ".gitattributes", attributesOid),
                ("100644", "notes.txt", textOid));
            var sha = _repo.CommitTree(tree, "normalization-sensitive candidate");
            Assert.False(File.Exists(filterMarker)); // the filter did not run during setup

            var workspace = Factory.Create(Request(sha));

            // No remote from the hostile global config appears in the agent repository: the
            // agent config file is remote-free AND git run under the disposable contract
            // (empty home, no global/system config) lists no remote.
            var agentConfig = File.ReadAllText(Path.Combine(workspace.SourcePath, ".git", "config"));
            Assert.DoesNotContain("[remote", agentConfig);
            Assert.DoesNotContain("hostile", agentConfig);
            Assert.Equal("",
                TestGitRepo.RunGitInIsolatedEnv(workspace.SourcePath, "remote").Trim());
            // The hostile init.templateDir marker never appears in the agent repository.
            Assert.Empty(Directory.GetFiles(
                workspace.SourcePath, "marker.txt", SearchOption.AllDirectories));
            // The hostile clean/smudge/process filter never executed.
            Assert.False(File.Exists(filterMarker));
            // Filter-free re-hashing: ingestion and agent trees equal the candidate tree
            // exactly despite eol/autocrlf settings.
            Assert.Equal(
                workspace.BaselineTreeOid,
                new GitService(workspace.TrustedIngestionPath).WriteTree());
            Assert.Equal(
                workspace.BaselineTreeOid,
                TestGitRepo.RunGitIn(workspace.SourcePath, "rev-parse", "HEAD^{tree}").Trim());
        }
        finally
        {
            foreach (var (key, value) in saved)
                Environment.SetEnvironmentVariable(key, value);
            hostileHome.Delete(recursive: true);
        }
    }

    // ---- Blocker 2: root validation before creation -------------------------------

    [Fact]
    public void Malformed_managed_roots_fail_before_creating_anything()
    {
        _repo.WriteFile("a.txt", "content");
        var sha = _repo.Commit("root validation fixture");

        foreach (var root in new[]
                 {
                     "relative-root", "", " ", "../escape", "/a/../b", "/double//slash",
                     "/trailing/", "/back\\slash", "C:/drive", "/non-nfc-cafe\u0301",
                     "/nonexistent-tenninety-root",
                 })
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                Factory.Create(Request(sha, managedRoot: root));
            });
        }
        // Nothing was created anywhere: neither the intended root nor any
        // traversal-resolved location received an attempt directory.
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
        Assert.False(Directory.Exists("/nonexistent-tenninety-root"));
    }

    [Fact]
    public void Root_home_shared_and_symlinked_managed_roots_fail()
    {
        _repo.WriteFile("a.txt", "content");
        var sha = _repo.Commit("root rejection fixture");

        Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha, managedRoot: "/"));
        });
        Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha, managedRoot: "/tmp")); // generic shared location
        });
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && home != "/" && Directory.Exists(home))
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                Factory.Create(Request(sha, managedRoot: home));
            });
        }

        // A symlinked root must be rejected…
        var rootLink = Path.Combine(_outside.Root, "root-link");
        Directory.CreateSymbolicLink(rootLink, _managedRoot.Root);
        Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha, managedRoot: rootLink));
        });

        // …as must a root with a symlink ancestor above it.
        var deeper = Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "deeper")).FullName;
        var ancestorLink = Path.Combine(_outside.Root, "ancestor-link");
        Directory.CreateSymbolicLink(ancestorLink, _managedRoot.Root);
        Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha, managedRoot: Path.Combine(ancestorLink, "deeper")));
        });

        // No attempt directory was ever created.
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_managedRoot.Root),
            e => Path.GetFileName(e).StartsWith("attempt-", StringComparison.Ordinal));
    }

    [Fact]
    public void Failure_after_attempt_creation_cleans_the_complete_attempt()
    {
        // A well-shaped but nonexistent commit SHA fails only AFTER the attempt, source and
        // ingestion directories have been created — proving the cleanup covers all children.
        var bogusSha = new string('a', 40);
        Assert.Throws<Tenninety.Git.GitException>(() =>
        {
            Factory.Create(Request(bogusSha));
        });
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    // ---- Blocker 3: bounded listing and blob transfer ------------------------------

    [Fact]
    public void Listing_budget_fails_closed()
    {
        _repo.WriteFile("a.txt", "content");
        var sha = _repo.Commit("listing budget fixture");

        Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha, limits: new MaterializationLimits
            {
                MaxTreeListingBytes = 10,
            }));
        });
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Oversized_blob_fails_promptly_with_the_limit_exception()
    {
        var big = new byte[10 * 1024 * 1024];
        new Random(11).NextBytes(big);
        _repo.WriteFile("big.bin", big);
        var sha = _repo.Commit("oversized blob fixture");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha, limits: new MaterializationLimits
            {
                MaxTotalBytes = 1024,
            }));
        });
        stopwatch.Stop();

        // The cap violation must surface immediately — never as a two-minute timeout.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"expected a prompt cap failure, took {stopwatch.Elapsed}");
        Assert.IsType<GitOutputLimitExceededException>(ex.InnerException);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Exact_limit_boundaries_still_succeed()
    {
        var content = "exact-boundary"; // 14 bytes
        _repo.WriteFile("a.txt", content);
        var sha = _repo.Commit("boundary fixture");
        var listingBytes = _repo.Git.LsTreeRecursiveRaw(sha, 1 << 20).Length;

        var workspace = Factory.Create(Request(sha, limits: new MaterializationLimits
        {
            MaxFiles = 1,
            MaxTotalBytes = Encoding.UTF8.GetByteCount(content),
            MaxTreeListingBytes = listingBytes,
        }));

        Assert.Equal(content, File.ReadAllText(Path.Combine(workspace.SourcePath, "a.txt")));
    }

    // ---- Finding 1: the candidate OID itself must be a commit ----------------------

    [Fact]
    public void Rejects_annotated_tag_object_id_even_when_it_peels_to_a_commit()
    {
        _repo.WriteFile("a.txt", "tagged content");
        var commitSha = _repo.Commit("tagged candidate");
        _repo.Run("tag", "-a", "v1", "-m", "annotated", commitSha);
        var tagObjectOid = TestGitRepo.RunGitIn(_repo.Root, "rev-parse", "v1").Trim();

        // The tag object is a real, resolvable 40-hex object — but not a commit.
        Assert.Equal(40, tagObjectOid.Length);
        Assert.NotEqual(commitSha, tagObjectOid);
        Assert.Equal("tag", TestGitRepo.RunGitIn(_repo.Root, "--no-replace-objects", "cat-file", "-t", tagObjectOid).Trim());

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(tagObjectOid));
        });
        Assert.Contains("not a commit", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    // ---- Finding 2: refs/replace must not redirect candidate reads -----------------

    [Fact]
    public void Replace_refs_cannot_redirect_the_candidate_commit()
    {
        _repo.WriteFile("a.txt", "A-original-content");
        var shaA = _repo.Commit("commit A");
        _repo.WriteFile("a.txt", "B-different-content");
        _repo.WriteFile("b.txt", "only in B");
        var shaB = _repo.Commit("commit B");

        _repo.Run("replace", shaA, shaB);
        try
        {
            // The expected tree is calculated INDEPENDENTLY of the materializer, with
            // replacements disabled and raw git.
            var expectedTree = TestGitRepo.RunGitIn(
                _repo.Root, "--no-replace-objects", "rev-parse", $"{shaA}^{{tree}}").Trim();

            var workspace = Factory.Create(Request(shaA));

            Assert.Equal("A-original-content",
                File.ReadAllText(Path.Combine(workspace.SourcePath, "a.txt")));
            Assert.False(File.Exists(Path.Combine(workspace.SourcePath, "b.txt")));
            Assert.Equal(expectedTree, workspace.BaselineTreeOid);
            Assert.Equal(shaA, workspace.Revision.CommitSha);
        }
        finally
        {
            _repo.Run("replace", "-d", shaA);
        }
    }

    [Fact]
    public void Replace_refs_cannot_redirect_candidate_blobs()
    {
        var xBytes = Encoding.UTF8.GetBytes("blob X original bytes");
        _repo.WriteFile("x.bin", xBytes);
        var sha = _repo.Commit("blob replace fixture");
        var oidX = GitTreeListingParser.Parse(
            _repo.Git.LsTreeRecursiveRaw(sha, 1 << 20), 1_000, 4_096).Single().ObjectSha;

        var yBytes = Encoding.UTF8.GetBytes("blob Y replacement bytes - completely different");
        var oidY = _repo.HashObject(yBytes);
        Assert.NotEqual(oidX, oidY);
        _repo.Run("replace", oidX, oidY);
        try
        {
            var expectedTree = TestGitRepo.RunGitIn(
                _repo.Root, "--no-replace-objects", "rev-parse", $"{sha}^{{tree}}").Trim();

            var workspace = Factory.Create(Request(sha));

            Assert.True(xBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(workspace.SourcePath, "x.bin"))));
            Assert.Equal(expectedTree, workspace.BaselineTreeOid);
        }
        finally
        {
            _repo.Run("replace", "-d", oidX);
        }
    }

    // ---- Finding 4: structural component preflight ---------------------------------

    [Fact]
    public void Case_colliding_directory_components_are_rejected()
    {
        var oidA = _repo.HashObject(Encoding.UTF8.GetBytes("in Dir"));
        var oidB = _repo.HashObject(Encoding.UTF8.GetBytes("in dir"));
        var tree = _repo.WriteRawTreeObject(
            ("100644", "Dir/a.txt", oidA),
            ("100644", "dir/b.txt", oidB));
        var sha = _repo.CommitTree(tree, "case-colliding directories");

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha));
        });
        Assert.Contains("case-colliding directory components", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void File_directory_prefix_conflicts_are_rejected()
    {
        var oid = _repo.HashObject(Encoding.UTF8.GetBytes("content"));
        var tree = _repo.WriteRawTreeObject(
            ("100644", "file", oid),
            ("100644", "file/child", oid));
        var sha = _repo.CommitTree(tree, "file/directory conflict");

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(sha));
        });
        Assert.Contains("file/directory prefix conflict", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));

        // The case-insensitive variant collides just the same.
        var caseTree = _repo.WriteRawTreeObject(
            ("100644", "file", oid),
            ("100644", "FILE/child", oid));
        var caseSha = _repo.CommitTree(caseTree, "case file/directory conflict");
        Assert.Throws<InvalidOperationException>(() =>
        {
            Factory.Create(Request(caseSha));
        });
        Assert.Empty(Directory.EnumerateFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Same_spelling_shared_directory_is_allowed()
    {
        _repo.WriteFile("dir/a.txt", "first");
        _repo.WriteFile("dir/b.txt", "second");
        _repo.WriteFile("dir/sub/c.txt", "nested");
        var sha = _repo.Commit("shared directory candidate");

        var workspace = Factory.Create(Request(sha));

        Assert.Equal("first", File.ReadAllText(Path.Combine(workspace.SourcePath, "dir/a.txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(workspace.SourcePath, "dir/b.txt")));
        Assert.Equal("nested", File.ReadAllText(Path.Combine(workspace.SourcePath, "dir/sub/c.txt")));
    }

    // ---- Finding 5: managed-root permissions ----------------------------------------

    [Fact]
    public void Group_or_world_writable_managed_root_is_rejected_before_attempt_creation()
    {
        _repo.WriteFile("a.txt", "content");
        var sha = _repo.Commit("writable root fixture");
        var sharedRoot = Directory.CreateDirectory(
            Path.Combine(_outside.Root, "shared-root")).FullName;
        var originalMode = File.GetUnixFileMode(sharedRoot);
        try
        {
            File.SetUnixFileMode(sharedRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);

            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                Factory.Create(Request(sha, managedRoot: sharedRoot));
            });
            Assert.Contains("group-writable or world-writable", ex.Message);
            // Rejected before any attempt directory was created.
            Assert.Empty(Directory.EnumerateFileSystemEntries(sharedRoot));
        }
        finally
        {
            // Restore permissions so the temp directory remains removable.
            File.SetUnixFileMode(sharedRoot, originalMode);
        }
    }
}
