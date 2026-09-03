
using System.Net.Sockets;
using System.Text;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Phase 3 scanner tests: no-follow regular-file semantics (symlinks, FIFOs, sockets,
/// hardlinks), nested/case-variant `.git` rejection, fixed role-transient exclusions,
/// NUL-delimited manifest cross-checks and scan-limit enforcement.
/// </summary>
public class CandidateScannerTests : IDisposable
{
    private readonly TestGitRepo _repo = new();
    private readonly TempDir _managedRoot = new();
    private readonly QuiescenceProof Proof;

    private CandidateWorkspace Workspace { get; }
    private string MainSha { get; }
    private string CandidateSha { get; }

    public CandidateScannerTests()
    {
        _repo.WriteFile("sentinel.txt", "sentinel\n");
        _repo.Commit("initial on main");
        MainSha = _repo.Git.HeadSha();
        _repo.Git.CreateAndCheckoutBranch("work/WP-001");
        _repo.WriteFile("src/existing.txt", "original\n");
        _repo.WriteFile("src/binary.bin", new byte[] { 1, 2, 3, 0, 255 });
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
        Proof = QuiescenceProof.Issue(
            "run-1", "attempt-1", SandboxRole.Coder, Workspace.AttemptRoot,
            "test harness: container stopped");
    }

    private void WriteWorkspaceFile(string relative, string content)
    {
        var path = Path.Combine(Workspace.SourcePath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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

    private CandidatePromotionResult Promote(CandidatePromotionOptions? options = null)
    {
        using var lease = DaemonLock.Acquire(_repo.Root);
        var service = new CandidatePromotionService(_repo.Git);
        return service.PromoteValidated(
            Workspace, Proof, options ?? new CandidatePromotionOptions(),
            new PromotionPreconditions(
                "work/WP-001", CandidateSha, MainSha, "WP-001: candidate [work package]"),
            lease);
    }

    [Fact]
    public void Post_materialization_symlinks_fail_closed()
    {
        var linkFile = Path.Combine(Workspace.SourcePath, "src/link.txt");
        var linkDir = Path.Combine(Workspace.SourcePath, "src/linkdir");
        var brokenLink = Path.Combine(Workspace.SourcePath, "src/broken.txt");
        File.CreateSymbolicLink(linkFile, "existing.txt");
        Directory.CreateSymbolicLink(linkDir, "sub");
        File.CreateSymbolicLink(brokenLink, "does-not-exist-anywhere");

        Assert.Throws<InvalidOperationException>(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof));
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Fifos_fail_closed_without_blocking()
    {
        var fifo = Path.Combine(Workspace.SourcePath, "src/pipe");
        using (var mkfifo = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
               {
                   FileName = "mkfifo",
                   WorkingDirectory = _repo.Root,
                   RedirectStandardOutput = true,
                   RedirectStandardError = true,
                   UseShellExecute = false,
                   CreateNoWindow = true,
                   ArgumentList = { fifo },
               }))
        {
            mkfifo!.WaitForExit();
            Assert.Equal(0, mkfifo.ExitCode);
        }
        Assert.True(File.Exists(fifo));

        var scanTask = Task.Run(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof));
        // Bounded by a short timeout: a blocking open would fail the test here.
        try
        {
            scanTask.Wait(TimeSpan.FromSeconds(30));
            Assert.Fail("the scan should have rejected the FIFO");
        }
        catch (AggregateException ex)
        {
            Assert.Contains("not a regular file", ex.InnerExceptions[0].Message);
        }
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
    }

    [Fact]
    public void Unix_sockets_fail_closed_without_blocking()
    {
        var socketPath = Path.Combine(Workspace.SourcePath, "src/daemon.sock");
        using var socket = new Socket(
            AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(socketPath));
        socket.Listen();
        Assert.True(File.Exists(socketPath));

        var scanTask = Task.Run(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof));
        try
        {
            scanTask.Wait(TimeSpan.FromSeconds(30));
            Assert.Fail("the scan should have rejected the socket");
        }
        catch (AggregateException ex)
        {
            Assert.Contains("not a regular file", ex.InnerExceptions[0].Message);
        }
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        socket.Close();
        if (File.Exists(socketPath)) File.Delete(socketPath);
    }

    [Fact]
    public void Hardlinked_files_promote_as_ordinary_content()
    {
        var originalPath = Path.Combine(Workspace.SourcePath, "src/existing.txt");
        var linkPath = Path.Combine(Workspace.SourcePath, "src/hardlink.txt");
        ProcessHardlink(originalPath, linkPath);

        // Prove the two paths really are the same inode (a true hardlink) while in the
        // workspace, using the no-follow reader's descriptor metadata.
        using var originalOpened = TrustedFileReader.OpenRegularFileNoFollow(originalPath);
        using var linkOpened = TrustedFileReader.OpenRegularFileNoFollow(linkPath);
        Assert.Equal(originalOpened.DeviceId, linkOpened.DeviceId);
        Assert.Equal(originalOpened.InodeId, linkOpened.InodeId);

        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.Equal("original\n", File.ReadAllText(WorktreePath("src/existing.txt")));
        Assert.Equal("original\n", File.ReadAllText(WorktreePath("src/hardlink.txt")));
        // The promoted result is ordinary repository content: after promotion the
        // authoritative file must NOT be a link to the (deleted) workspace inode. The
        // authoritative worktree lives on the repo filesystem; the workspace hardlink pair
        // is deleted with the attempt, so a preserved link relationship would break reads.
        Assert.Equal("original\n", File.ReadAllText(WorktreePath("src/hardlink.txt")));
        var commitContent = TestGitRepo.RunGitIn(
            _repo.Root, "show", $"HEAD:src/hardlink.txt");
        Assert.Equal("original\n", commitContent);
    }

    private static bool ProcessHardlink(string existing, string link)
    {
        using var ln = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ln",
            WorkingDirectory = "/",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { existing, link },
        });
        ln!.WaitForExit();
        if (ln.ExitCode != 0)
            throw new InvalidOperationException("test fixture hardlink creation failed");
        return true;
    }

    // ---- Scan and policy/patch limits --------------------------------------------------

    [Fact]
    public void Nested_git_entries_reject_the_whole_candidate()
    {
        Directory.CreateDirectory(Path.Combine(Workspace.SourcePath, "src/.git"));
        WriteWorkspaceFile("src/.git/config", "[core] bare = false");
        Assert.Throws<InvalidOperationException>(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof));

        DeleteWorkspaceFile("src/.git/config");
        Directory.Delete(Path.Combine(Workspace.SourcePath, "src/.git"));

        Directory.CreateDirectory(Path.Combine(Workspace.SourcePath, "src/.GIT"));
        WriteWorkspaceFile("src/.GIT/config", "case variant");
        Assert.Throws<InvalidOperationException>(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof));

        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        Assert.True(_repo.Git.IsClean());
    }

    [Fact]
    public void Transient_directories_are_skipped_as_whole_subtrees()
    {
        WriteWorkspaceFile(".opencode/agent-state.json", "transient dir content");
        WriteWorkspaceFile(".pi/session.json", "transient pi dir");
        WriteWorkspaceFile(".aider.history/x.md", "transient aider dir");
        WriteWorkspaceFile("src/real.txt", "real work");

        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.DoesNotContain(result.Patch!.Changes,
            c => c.NormalizedPath.StartsWith(".opencode", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Patch.Changes,
            c => c.NormalizedPath.StartsWith(".pi/", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Patch.Changes,
            c => c.NormalizedPath.StartsWith(".aider.history", StringComparison.Ordinal));
        Assert.Contains(result.Patch.Changes, c => c.NormalizedPath == "src/real.txt");
        Assert.False(File.Exists(WorktreePath(".opencode/agent-state.json")));
        Assert.True(File.Exists(WorktreePath("src/real.txt")));
    }

    [Fact]
    public void Transient_files_are_skipped_without_over_matching()
    {
        WriteWorkspaceFile(".opencode", "transient opencode file");
        WriteWorkspaceFile(".pi", "transient pi file");
        WriteWorkspaceFile(".aider.chat.history.md", "transient history");
        WriteWorkspaceFile(".aider.tags.yml", "transient tags");
        // Non-transient paths that must NOT be hidden by an over-broad match.
        WriteWorkspaceFile("src/.aider.notes", "real work in a nested path");
        WriteWorkspaceFile(".opencode-backup", "real work with a similar name");

        var result = Promote();

        Assert.False(result.NoChanges);
        Assert.DoesNotContain(result.Patch!.Changes,
            c => c.NormalizedPath is ".opencode" or ".pi" or
                 ".aider.chat.history.md" or ".aider.tags.yml");
        Assert.Contains(result.Patch.Changes,
            c => c.NormalizedPath == "src/.aider.notes");
        Assert.Contains(result.Patch.Changes,
            c => c.NormalizedPath == ".opencode-backup");
        Assert.False(File.Exists(WorktreePath(".opencode")));
        Assert.True(File.Exists(WorktreePath("src/.aider.notes")));
        Assert.True(File.Exists(WorktreePath(".opencode-backup")));
    }

    // ---- B9: sensitive-path classification table ---------------------------------------

    public static TheoryData<string> SensitivePaths => new()
    {
        "Dockerfile", "Dockerfile.dev", "docker-compose.yml", "docker-compose.yaml",
        "compose.yml", "compose.test.yaml", ".github/workflows/x.yml", ".gitlab-ci.yml",
        ".drone.yml", ".travis.yml", "azure-pipelines.yml", "Jenkinsfile",
        ".circleci/config.yml", ".gitmodules", ".gitattributes", "NuGet.config",
        "Directory.Build.props", "Directory.Packages.props", "global.json",
        "paket.dependencies", "paket.lock", ".npmrc", ".yarnrc", ".yarnrc.yml",
        ".pnpmfile.cjs", ".pypirc", "pip.conf", "pip.ini", "settings.xml",
        "gradle.properties", ".cargo/config.toml",
    };

    [Theory]
    [MemberData(nameof(SensitivePaths))]
    public void Sensitive_paths_reject_by_default_and_accept_only_exact_allowlisting(string path)
    {
        var content = $"content of {path}";
        WriteWorkspaceFile(path, content);

        // Default: rejected.
        var ex = Assert.Throws<CandidatePolicyRejectedException>(() => Promote());
        Assert.Contains("sensitive path", ex.Message);
        Assert.Contains(path, ex.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());

        // A case-mismatched allowlist entry must not authorize another spelling.
        var wrongCase = path.ToUpperInvariant() != path
            ? path.ToUpperInvariant()
            : path.ToLowerInvariant();
        if (!string.Equals(wrongCase, path, StringComparison.Ordinal))
        {
            DeleteWorkspaceFile(path);
            WriteWorkspaceFile(path, content);
            var wrongCaseOptions = new CandidatePromotionOptions
            {
                Policy = new PromotionPolicyOptions { AllowSensitivePaths = [wrongCase] },
            };
            Assert.Throws<CandidatePolicyRejectedException>(() => Promote(wrongCaseOptions));
            DeleteWorkspaceFile(path);
        }

        // Exact case-sensitive allowlist entry: accepted.
        WriteWorkspaceFile(path, content);
        var allowedOptions = new CandidatePromotionOptions
        {
            Policy = new PromotionPolicyOptions { AllowSensitivePaths = [path] },
        };
        var result = Promote(allowedOptions);
        Assert.False(result.NoChanges);
        Assert.True(File.Exists(WorktreePath(path)));
    }

    [Fact]
    public void Manifest_equals_the_independent_nul_staged_diff_and_write_tree()
    {
        WriteWorkspaceFile("src/existing.txt", "modified\n");
        WriteWorkspaceFile("src/added.bin", new byte[] { 0, 1, 2, 250 });
        DeleteWorkspaceFile("src/doomed.txt");

        var scan = new CandidateScanner(_repo.Git).Scan(Workspace, Proof);

        // Independent NUL-delimited staged diff, parsed without the method under test.
        var raw = TestGitRepo.RunGitInIsolatedEnv(
            Workspace.TrustedIngestionPath, "diff", "--cached", "--raw", "-z",
            "--no-renames", "--no-ext-diff", "--no-textconv", Workspace.BaselineTreeOid);
        var fields = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var independent = new Dictionary<string, string>();
        for (var i = 0; i + 1 < fields.Length; i += 2)
        {
            var meta = fields[i];
            var path = fields[i + 1];
            var status = meta.Split(' ')[4];
            independent[path] = status;
        }
        Assert.Equal(independent.Keys.OrderBy(k => k, StringComparer.Ordinal),
            scan.Changes.Select(c => c.NormalizedPath));
        foreach (var change in scan.Changes)
            Assert.Equal(independent[change.NormalizedPath],
                change.Kind switch
                {
                    GitChangeKind.Added => "A",
                    GitChangeKind.Modified => "M",
                    _ => "D",
                });

        // The index tree is exactly the scanned target tree.
        var writeTree = TestGitRepo.RunGitInIsolatedEnv(
            Workspace.TrustedIngestionPath, "write-tree").Trim();
        Assert.Equal(scan.TargetTreeOid, writeTree);
    }

    // ---- B3: staged-byte secret scanning ---------------------------------------------

    [Fact]
    public void Scan_limits_are_enforced_independently()
    {
        // Per-file bytes.
        WriteWorkspaceFile("src/big.bin", new byte[2 * 1024 * 1024]);
        var perFile = Assert.Throws<InvalidOperationException>(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof,
                new CandidateScanLimits { MaxFileBytes = 1024 }));
        Assert.Contains("per-file maximum", perFile.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
        DeleteWorkspaceFile("src/big.bin");

        // Aggregate bytes.
        WriteWorkspaceFile("src/one.bin", new byte[600 * 1024]);
        WriteWorkspaceFile("src/two.bin", new byte[600 * 1024]);
        var aggregate = Assert.Throws<InvalidOperationException>(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof,
                new CandidateScanLimits { MaxTotalBytes = 1024 * 1024 }));
        Assert.Contains("aggregate byte budget", aggregate.Message);
        DeleteWorkspaceFile("src/one.bin");
        DeleteWorkspaceFile("src/two.bin");

        // File count: the workspace holds 3 tracked files + 3 new = 6. Exactly 6 succeeds;
        // any lower bound fails closed while staging.
        WriteWorkspaceFile("f1.txt", "1");
        WriteWorkspaceFile("f2.txt", "2");
        WriteWorkspaceFile("f3.txt", "3");
        var withinLimit = new CandidateScanner(_repo.Git).Scan(Workspace, Proof,
            new CandidateScanLimits { MaxFiles = 6 });
        Assert.Equal(6, withinLimit.TargetEntries.Count);
        var strict = Assert.Throws<InvalidOperationException>(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof,
                new CandidateScanLimits { MaxFiles = 4 }));
        Assert.Contains("maximum of 4 files", strict.Message);
        foreach (var f in new[] { "f1.txt", "f2.txt", "f3.txt" }) DeleteWorkspaceFile(f);

        // Path byte length: nested components keep each OS name valid while the total
        // relative path exceeds the configured limit.
        var deep = string.Join('/', Enumerable.Repeat(new string('d', 60), 6));
        WriteWorkspaceFile(deep + "/leaf.txt", "deep long path");
        var pathLimit = Assert.Throws<InvalidOperationException>(() =>
            new CandidateScanner(_repo.Git).Scan(Workspace, Proof,
                new CandidateScanLimits { MaxPathBytes = 256 }));
        Assert.Contains("configured length", pathLimit.Message);
        Assert.Equal(CandidateSha, _repo.Git.HeadSha());
    }


    public void Dispose()
    {
        _managedRoot.Dispose();
        _repo.Dispose();
    }
}
