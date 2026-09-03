using Tenninety.Core;

namespace Tenninety.Git;

public sealed class GitException(string command, int exitCode, string stderr)
    : Exception($"git '{command}' failed (exit {exitCode}): {stderr.Trim()}");

public record GitCommit(string Sha, string Subject, string Author, string Date);

/// <summary>Git-first state operations (Part I.2 principle 4, Part VI.3 safety rules).</summary>
public interface IGitService
{
    string RepoPath { get; }
    bool IsRepository();
    void Init();
    bool IsClean();
    bool IsPathClean(string relativePath);
    string CurrentBranch();
    bool BranchExists(string branch);
    void CreateAndCheckoutBranch(string branch);
    void CheckoutBranch(string branch);
    /// <summary>Integrates the current main tip into an existing work branch before resuming.</summary>
    void MergeMainIntoCurrentBranch();
    /// <summary>Stages all changes and commits. Returns null when the tree was clean.</summary>
    string? CommitAll(string message);
    /// <summary>Stages only the given paths and commits them; null when nothing changed.</summary>
    string? CommitPaths(IEnumerable<string> relativePaths, string message);
    /// <summary>Squash-merges a work branch into main as ONE identifiable commit; returns the new sha.</summary>
    string SquashMergeToMain(string branch, string message);
    /// <summary>Bounded unified patch of branch vs main (head+tail elided).</summary>
    string DiffPatchAgainstMain(string branch, int maxChars = 20000);
    string HeadSha();
    string DiffHeadStat();
    string DiffAgainstMain(string branch);
    IReadOnlyList<GitCommit> RecentCommits(int count);
    GitCommit? FindCommit(string shaOrRef);
    /// <summary>Mechanical revert of a commit as a new commit (no history rewrite).</summary>
    void RevertCommitNoEdit(string sha);
    /// <summary>True when the commit is reachable from the current main branch.</summary>
    bool IsAncestorOfMain(string sha);
    /// <summary>Deletes a branch. Force skips the merged-check (used right after a squash
    /// merge, where content is provably on main even though -d would refuse).</summary>
    void DeleteBranchSafe(string branch, bool force = false);

    // ---- Small trusted primitives for candidate materialization (tree/object machinery).
    // Sandbox lifecycle does NOT live here; these only resolve commits/trees, hash and read
    // objects in a binary-safe, size-capped, filter-free way. ----

    /// <summary>Resolves a full commit SHA to its tree OID. Validates that the SHA really is a
    /// commit (peels ^{commit} first) before returning <commit>^{tree}.</summary>
    string ResolveTreeOfCommit(string commitSha);

    /// <summary>Raw `git ls-tree -r -z --full-tree` output bytes for a commit: NUL-delimited
    /// records, no quoting, safe for byte-accurate parsing of unusual paths. The output is
    /// hard-capped at maxBytes: a longer listing kills the process immediately instead of
    /// buffering unboundedly.</summary>
    byte[] LsTreeRecursiveRaw(string commitSha, long maxBytes);

    /// <summary>Binary-safe blob read by object ID (never path-based). Fails closed with
    /// <see cref="GitOutputLimitExceededException"/> when the object exceeds maxBytes, before
    /// the whole object is buffered.</summary>
    byte[] ReadBlobRaw(string objectSha, long maxBytes);

    /// <summary>Streams one blob (by object ID) directly into a NEW destination file under a
    /// hard byte cap — no blob-sized allocation. A cap violation or git failure kills the
    /// process promptly and removes the partial file.</summary>
    /// <returns>The number of bytes written.</returns>
    long WriteBlobToFile(string objectSha, string destinationPath, long maxBytes);

    /// <summary>Hashes a file's exact on-disk bytes WITHOUT any clean/smudge/eol filters
    /// (`git hash-object -w --no-filters`) into this repository's own object store and
    /// returns the resulting blob OID.</summary>
    string HashObjectNoFilters(string filePath);

    /// <summary>Adds one already-validated index entry without touching any worktree
    /// (`git update-index --add --cacheinfo &lt;mode&gt;,&lt;oid&gt;,&lt;path&gt;`).</summary>
    void UpdateIndexCacheInfo(string mode, string objectSha, string path);

    /// <summary>Sets one local (repository-level) config value.</summary>
    void SetLocalConfig(string key, string value);

    /// <summary>Removes one path from the index without touching the worktree
    /// (`git update-index --force-remove`): used by the trusted scanner to record deletions
    /// of files the agent removed from its disposable workspace.</summary>
    void RemoveFromIndex(string relativePath);

    /// <summary>Raw binary/full-index diff between two tree objects, with external diffs,
    /// rename detection and color disabled (renames are represented as delete plus add).
    /// Output is hard-capped at maxBytes.</summary>
    byte[] DiffTreesRaw(string oldTreeSha, string newTreeSha, long maxBytes);

    /// <summary>NUL-delimited RAW name-status diff between two tree objects — the
    /// independent cross-check manifest source for the extraction scan
    /// (`--no-replace-objects diff --raw -z --abbrev=40 --no-renames --no-ext-diff
    /// --no-textconv --no-color`).</summary>
    byte[] TreeDiffNamesRaw(string oldTreeSha, string newTreeSha, long maxBytes);

    /// <summary>Checked preflight for a validated patch: verifies it would apply cleanly to
    /// index AND worktree without changing anything. Whitespace settings are pinned so host
    /// config cannot fail or rewrite the patch.</summary>
    void VerifyPatchApplies(string patchFilePath);

    /// <summary>Applies a validated patch ATOMICALLY to index and worktree
    /// (`git apply --index`): either every change lands or nothing does.</summary>
    void ApplyPatchToIndexAndWorktree(string patchFilePath);

    /// <summary>Restores exactly one path (index and worktree) from a SPECIFIC commit — used
    /// by the promotion rollback to restore validated paths from the recorded pre-apply
    /// commit (HEAD may already have moved if a commit succeeded).</summary>
    void RestorePathFromCommit(string commitSha, string relativePath);

    /// <summary>Loads an EMPTY tree into the index (`git read-tree --empty`): every trusted
    /// extraction scan starts from a fresh index state so no staged entry from an earlier
    /// scan — in particular a policy-rejected addition — can ever survive a retry.</summary>
    void ReadTreeEmpty();

    /// <summary>Raw NUL-delimited staged diff of the index against the given baseline tree:
    /// `--no-replace-objects diff --cached --raw -z --no-renames --no-ext-diff --no-textconv`.
    /// Output is hard-capped at maxBytes. This is the authoritative staged change manifest
    /// source; parsing operates on bytes, never on line-split display text.</summary>
    byte[] StagedDiffRaw(string baselineTreeSha, long maxBytes);

    /// <summary>Ingests a file's exact bytes by streaming the CALLER-OWNED opened handle into
    /// `git hash-object -w --no-filters --stdin` — Git never reopens the workspace pathname.
    /// Enforces maxBytes on the bytes actually streamed (killing Git promptly on violation)
    /// and captures at most maxInspectedPrefixBytes of the exact ingested bytes so the
    /// promotion policy can scan the bytes the staged object really contains.</summary>
    HashedIngestion HashObjectNoFiltersFromStream(
        Stream source, long maxBytes, int maxInspectedPrefixBytes);

    /// <summary>Checked preflight for validated patch BYTES: verifies they would apply
    /// cleanly to index and worktree. The bytes are piped through standard input — the same
    /// immutable bytes are used for check and apply; Git never reopens a mutable path.</summary>
    void VerifyPatchBytesApplyToIndexAndWorktree(byte[] patchBytes);

    /// <summary>Applies validated patch BYTES atomically to index and worktree via standard
    /// input (`git apply --index --stdin`).</summary>
    void ApplyPatchBytesToIndexAndWorktree(byte[] patchBytes);

    /// <summary>Compare-and-swap ref update: moves <paramref name="refName"/> to newSha ONLY
    /// if it currently points exactly at expectedSha (used by the promotion rollback).</summary>
    void UpdateRefCompareAndSwap(string refName, string newSha, string expectedSha);

    /// <summary>Commits the index with TENNINETY's pinned author and committer identity
    /// (user.name=tenninety, user.email=tenninety@localhost, no signing), overriding any
    /// repository, global or environment identity. The message must be a trusted,
    /// control-character-free string.</summary>
    string CommitIndexWithTenninetyIdentity(string message);

    /// <summary>Creates a COMMIT OBJECT for the verified tree with exactly one parent
    /// (<paramref name="parentCommitSha"/>) and the trusted Tenninety identity/message,
    /// WITHOUT moving any ref. Returns the new commit OID so the caller can verify tree and
    /// parent before advancing the work-branch ref via a compare-and-swap update.</summary>
    string CreateCommitObjectForTree(string treeSha, string parentCommitSha, string message);

    /// <summary>Resolves the parent commit OID of a commit (replacements disabled).</summary>
    string ResolveCommitParent(string commitSha);

    /// <summary>Stages every file in the working tree (`add --force --all`): ignore rules are
    /// overridden so a staged tree is exactly the working tree. Filter-aware staging is never
    /// used to verify a materialized baseline; trusted callers only.</summary>
    void StageAll();

    /// <summary>Writes the current index as a tree object and returns its OID.</summary>
    string WriteTree();

    /// <summary>Commits only what is staged; null when nothing is staged.</summary>
    string? CommitStaged(string message);

    /// <summary>Commits the index, creating an empty commit when nothing is staged.</summary>
    string CommitAllowEmpty(string message);
}

/// <summary>A blob ingested through the no-follow streaming pump: the resulting object ID
/// and the exact bounded prefix of the ingested bytes (for the promotion policy's
/// content scan), bound together at ingestion time.</summary>
public sealed record HashedIngestion(string ObjectSha, byte[] InspectedPrefix, long BytesRead);

/// <summary>A git object read exceeded the configured byte cap and was not read further.</summary>
public sealed class GitOutputLimitExceededException(long maxBytes)
    : InvalidOperationException($"git output exceeded the configured {maxBytes}-byte read cap.");

public sealed class GitService : IGitService
{
    private static readonly string[] EnvironmentAllowlist =
    [
        "PATH", "HOME", "LANG", "LC_ALL", "USER", "LOGNAME", "TMPDIR",
        "SSL_CERT_FILE", "SSL_CERT_DIR", "XDG_CONFIG_HOME",
    ];

    // Empty, Tenninety-owned directories for the disposable execution profile: HOME points at
    // an empty home and `git init` uses an empty template directory, so no host template hooks
    // or other template content can ever enter a disposable repository. Both are created
    // owner-only (0700-equivalent), verified to be real non-symlinked directories, and
    // verified empty — failing closed if any guarantee cannot be established. Internal so the
    // security-verification tests can inspect them; never logged.
    internal static readonly string DisposableHomeDirectory =
        CreateEmptyDisposableDirectory("tenninety-git-home");

    internal static readonly string DisposableTemplateDirectory =
        CreateEmptyDisposableDirectory("tenninety-git-template");

    private static string CreateEmptyDisposableDirectory(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Owner-only permissions (0700-equivalent): no group and no other access.
            File.SetUnixFileMode(dir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var mode = File.GetUnixFileMode(dir);
            if (mode.HasFlag(UnixFileMode.GroupWrite) ||
                mode.HasFlag(UnixFileMode.OtherWrite) ||
                mode.HasFlag(UnixFileMode.GroupRead) ||
                mode.HasFlag(UnixFileMode.OtherRead) ||
                mode.HasFlag(UnixFileMode.GroupExecute) ||
                mode.HasFlag(UnixFileMode.OtherExecute))
                throw new InvalidOperationException(
                    "the disposable git support directory could not be secured to owner-only " +
                    "permissions; failing closed.");
        }
        var info = new DirectoryInfo(dir);
        if (!info.Exists || info.LinkTarget is not null)
            throw new InvalidOperationException(
                "the disposable git support directory is not a real directory (symlink or " +
                "missing); failing closed.");
        if (Directory.EnumerateFileSystemEntries(dir).Any())
            throw new InvalidOperationException(
                "the disposable git support directory must be empty; failing closed.");
        return dir;
    }

    private readonly bool _isolated;

    public string RepoPath { get; }

    /// <summary>Authoritative-repository profile: unchanged behavior, allowlisted environment.</summary>
    public GitService(string repoPath) : this(repoPath, isolated: false) { }

    private GitService(string repoPath, bool isolated)
    {
        RepoPath = Path.GetFullPath(repoPath);
        _isolated = isolated;
    }

    /// <summary>
    /// Disposable (ingestion/agent) repository profile: every git command runs fully isolated
    /// from the host — no inherited environment except PATH, no HOME content (empty trusted
    /// home), GIT_CONFIG_GLOBAL=/dev/null, GIT_CONFIG_SYSTEM=/dev/null,
    /// GIT_CONFIG_NOSYSTEM=1 (defensive), no terminal prompting, hooks disabled, and `init`
    /// uses the known-empty trusted template directory. No GIT_DIR, GIT_WORK_TREE,
    /// GIT_OBJECT_DIRECTORY, credential, SSH, alternate or other Git control variable can be
    /// inherited, so global remotes, templates, filters and identities can never leak in.
    /// </summary>
    public static GitService CreateDisposable(string repoPath) => new(repoPath, isolated: true);

    public bool IsRepository()
    {
        var dir = Path.Combine(RepoPath, ".git");
        return Directory.Exists(dir) || File.Exists(dir);
    }

    public void Init()
    {
        if (_isolated)
        {
            if (Directory.EnumerateFileSystemEntries(DisposableTemplateDirectory).Any())
                throw new InvalidOperationException(
                    "the trusted disposable git template directory is not empty; failing closed.");
            Run("init", "--template", DisposableTemplateDirectory, "-b", TenNinety.MainBranch);
        }
        else
            Run("init", "-b", TenNinety.MainBranch);
        // Ensure commits are possible even on machines without global git identity. In the
        // isolated profile global/system config is /dev/null, so the probe only sees local
        // config and a fresh local identity is always established.
        if (!HasIdentity())
        {
            Run("config", "user.name", "tenninety");
            Run("config", "user.email", "tenninety@localhost");
        }
    }

    private bool HasIdentity() =>
        TryRun("config", "user.name").ExitCode == 0 && TryRun("config", "user.email").ExitCode == 0;

    public bool IsClean() => Run("status", "--porcelain").Output.Trim().Length == 0;

    public bool IsPathClean(string relativePath) =>
        Run("status", "--porcelain", "--", relativePath).Output.Trim().Length == 0;

    public string CurrentBranch() => Run("rev-parse", "--abbrev-ref", "HEAD").Output.Trim();

    public bool BranchExists(string branch) =>
        TryRun("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;

    public void CreateAndCheckoutBranch(string branch)
    {
        if (BranchExists(branch))
            throw new InvalidOperationException($"branch '{branch}' already exists — refusing to reuse stale state.");
        Run("checkout", "-b", branch);
    }

    public void CheckoutBranch(string branch) => Run("checkout", branch);

    public void MergeMainIntoCurrentBranch()
    {
        (int ExitCode, string Output, string Stderr) result;
        try
        {
            result = TryRun("merge", "--no-edit", TenNinety.MainBranch);
        }
        catch
        {
            TryAbort("merge");
            throw;
        }
        if (result.ExitCode == 0) return;
        TryAbort("merge");
        throw new GitException($"merge --no-edit {TenNinety.MainBranch}", result.ExitCode, result.Stderr);
    }

    public string? CommitAll(string message)
    {
        Run("add", "-A");
        ExcludeSecretShapedStagedFiles();
        return CommitStaged(message);
    }

    /// <summary>Secret-shaped NEW files are unstaged and repo-locally excluded instead of being
    /// committed (Part VI). Already-tracked modifications stay staged – hiding those would be
    /// misleading, since their history exists regardless.</summary>
    private void ExcludeSecretShapedStagedFiles()
    {
        // Check additions relative to HEAD. Asking ls-files after `git add` cannot distinguish
        // a newly-added secret from a previously tracked file because both are in the index.
        var additions = TryRun("diff", "--cached", "--diff-filter=A", "--name-only", "-z").Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        foreach (var path in additions)
        {
            if (!Core.Security.Sanitizer.IsExcludedFile(path)) continue;
            Run("rm", "--cached", "--ignore-unmatch", "--", path);
            EnsureSecretPatternsExcluded(path);
        }
    }

    private void EnsureSecretPatternsExcluded(string path)
    {
        var infoExclude = ResolveGitPath("info/exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(infoExclude)!);
        var existing = File.Exists(infoExclude)
            ? File.ReadAllLines(infoExclude).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var required = Core.Security.Sanitizer.ExcludedFilePatterns
            .Append(GitIgnoreLiteral(path));
        var missing = required
            .Where(pattern => !existing.Contains(pattern))
            .ToArray();
        if (missing.Length > 0) File.AppendAllLines(infoExclude, missing);
    }

    private static string GitIgnoreLiteral(string path)
    {
        if (path.IndexOfAny(['\r', '\n']) >= 0) return "*secret*";
        var escaped = new System.Text.StringBuilder("/");
        foreach (var c in path.Replace('\\', '/'))
        {
            if (c is '*' or '?' or '[' or ']' or ' ') escaped.Append('\\');
            escaped.Append(c);
        }
        return escaped.ToString();
    }

    /// <summary>Stages ONLY the given paths (repo-relative) and commits them, so framework
    /// commands never capture unrelated user work. Returns null when nothing changed.</summary>
    public string? CommitPaths(IEnumerable<string> relativePaths, string message)
    {
        var paths = relativePaths.Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) return null;
        foreach (var path in paths) Run("add", "--", path);

        var diffArgs = new List<string> { "diff", "--cached", "--quiet", "--" };
        diffArgs.AddRange(paths);
        if (TryRun(diffArgs.ToArray()).ExitCode == 0) return null;

        // --only constructs the commit from these paths and leaves unrelated staged user
        // work in the index instead of silently capturing it in a framework commit.
        var commitArgs = new List<string> { "commit", "--no-verify", "--only", "-m", message, "--" };
        commitArgs.AddRange(paths);
        Run(commitArgs.ToArray());
        return HeadSha();
    }

    /// <summary>
    /// Squash-merges the work branch into main as ONE identifiable commit and returns its sha.
    /// Always squashing guarantees that reverting the promotion commit reverts the complete
    /// work package, even when earlier attempts left several commits on the branch.
    /// </summary>
    public string SquashMergeToMain(string branch, string message)
    {
        if (!IsClean())
            throw new InvalidOperationException("work branch must be clean before promotion.");
        CheckoutBranch(TenNinety.MainBranch);
        var result = TryRun("merge", "--squash", branch);
        if (result.ExitCode != 0)
            throw new GitException($"merge --squash {branch}", result.ExitCode, result.Stderr);
        // The squash merge already populated the index. Commit that reviewed index exactly;
        // never run `git add -A` here, which could capture post-review untracked files.
        var sha = CommitStaged(message)
            ?? throw new InvalidOperationException($"squash merge of '{branch}' produced no changes.");
        return sha;
    }

    public string HeadSha() => Run("rev-parse", "HEAD").Output.Trim();

    public string DiffHeadStat() => Run("diff", "HEAD", "--stat").Output;

    public string DiffAgainstMain(string branch) =>
        Run("diff", $"{TenNinety.MainBranch}...{branch}", "--stat").Output;

    /// <summary>Bounded unified patch of branch vs main; long patches keep head and tail with
    /// an elision marker so a model sees both the opening context and the latest changes.</summary>
    public string DiffPatchAgainstMain(string branch, int maxChars = 20000)
    {
        var patch = Run("diff", $"{TenNinety.MainBranch}...{branch}").Output;
        if (patch.Length <= maxChars) return patch;
        var headLen = maxChars * 3 / 4;
        var tailLen = maxChars - headLen;
        return patch[..headLen] + "\n… [diff truncated – showing head and tail] …\n" + patch[^tailLen..];
    }

    public IReadOnlyList<GitCommit> RecentCommits(int count)
    {
        var output = Run("log", $"-{count}", "--pretty=format:%H%x1f%s%x1f%an%x1f%aI").Output;
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\x1f'))
            .Where(p => p.Length == 4)
            .Select(p => new GitCommit(p[0], p[1], p[2], p[3]))
            .ToList();
    }

    public GitCommit? FindCommit(string shaOrRef)
    {
        var r = TryRun("show", "-s", "--pretty=format:%H%x1f%s%x1f%an%x1f%aI", shaOrRef);
        if (r.ExitCode != 0) return null;
        var p = r.Output.Trim().Split('\x1f');
        return p.Length == 4 ? new GitCommit(p[0], p[1], p[2], p[3]) : null;
    }

    public void RevertCommitNoEdit(string sha)
    {
        try
        {
            Run("revert", "--no-edit", sha);
        }
        catch
        {
            TryAbort("revert");
            throw;
        }
    }

    /// <summary>merge-base –is-ancestor: exit 0 proves the commit is on main.</summary>
    public bool IsAncestorOfMain(string sha) =>
        TryRun("merge-base", "--is-ancestor", sha, TenNinety.MainBranch).ExitCode == 0;

    public void DeleteBranchSafe(string branch, bool force = false) =>
        Run("branch", force ? "-D" : "-d", branch);

    /// <summary>Prefixes candidate object commands with `--no-replace-objects` (a global
    /// option, correctly positioned before the subcommand) so refs/replace can never redirect
    /// an exact candidate read. Centralizing it here makes accidental omission difficult.</summary>
    private static string[] NoReplace(params string[] args) =>
        ["--no-replace-objects", .. args];

    /// <summary>Requires the exact 40-hex object ID itself to BE a commit object
    /// (`cat-file -t` must answer exactly "commit" with replacements disabled). An annotated
    /// tag that peels to a commit, a tree, a blob or a nonexistent ID all fail; refs/replace
    /// cannot redirect the check.</summary>
    private void RequireCommitObject(string objectSha)
    {
        var type = Run(NoReplace("cat-file", "-t", objectSha)).Output.Trim();
        if (type != "commit")
            throw new InvalidOperationException(
                $"the candidate object {objectSha} is a '{type}' object, not a commit: the " +
                "candidate SHA must identify the exact commit object itself (annotated tags " +
                "that peel to a commit are rejected).");
    }

    public string ResolveTreeOfCommit(string commitSha)
    {
        RequireCommitObject(commitSha);
        return Run(NoReplace("rev-parse", "--verify", $"{commitSha}^{{tree}}")).Output.Trim();
    }

    public byte[] LsTreeRecursiveRaw(string commitSha, long maxBytes) =>
        RunRaw(NoReplace("ls-tree", "-r", "-z", "--full-tree", commitSha), maxBytes);

    public byte[] ReadBlobRaw(string objectSha, long maxBytes) =>
        RunRaw(NoReplace("cat-file", "blob", objectSha), maxBytes);

    public long WriteBlobToFile(string objectSha, string destinationPath, long maxBytes)
    {
        using var proc = System.Diagnostics.Process.Start(
            BuildGitStartInfo(NoReplace("cat-file", "blob", objectSha)))
            ?? throw new InvalidOperationException("failed to start git process.");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var exitTask = Task.Run(() => proc.WaitForExit());
        long total = 0;
        try
        {
            using (var destination = new FileStream(
                destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Streaming pump: stdout is drained continuously, so git can never block on
                // a full pipe; the cap is enforced inside the pump and kills the process
                // immediately (no blob-sized allocation, no two-minute waits).
                var buffer = new byte[81920];
                while (true)
                {
                    var read = proc.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length);
                    if (read == 0) break;
                    total += read;
                    if (total > maxBytes) throw new GitOutputLimitExceededException(maxBytes);
                    destination.Write(buffer, 0, read);
                }
            }
            if (!exitTask.Wait((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
                throw new TimeoutException("git cat-file did not exit after streaming the blob.");
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
                throw new GitException($"cat-file blob {objectSha}", proc.ExitCode, stderr);
            return total;
        }
        catch
        {
            KillAndReap(proc);
            try { File.Delete(destinationPath); } catch { /* best-effort partial removal */ }
            throw;
        }
    }

    public string HashObjectNoFilters(string filePath) =>
        Run("hash-object", "-w", "--no-filters", "--", filePath).Output.Trim();

    public void UpdateIndexCacheInfo(string mode, string objectSha, string path) =>
        Run("update-index", "--add", "--cacheinfo", $"{mode},{objectSha},{path}");

    public void SetLocalConfig(string key, string value) => Run("config", key, value);

    public void RemoveFromIndex(string relativePath) =>
        Run("update-index", "--force-remove", "--", relativePath);

    public byte[] DiffTreesRaw(string oldTreeSha, string newTreeSha, long maxBytes) =>
        RunRaw(NoReplace("diff", "--binary", "--full-index", "--no-ext-diff",
            "--no-renames", "--no-color", "--abbrev=40", oldTreeSha, newTreeSha), maxBytes);

    public byte[] TreeDiffNamesRaw(string oldTreeSha, string newTreeSha, long maxBytes) =>
        RunRaw(NoReplace("diff", "--raw", "-z", "--abbrev=40", "--no-renames",
            "--no-ext-diff", "--no-textconv", "--no-color", oldTreeSha, newTreeSha), maxBytes);

    public void VerifyPatchApplies(string patchFilePath) =>
        Run("-c", "apply.whitespace=nowarn", "apply", "--check", "--index", patchFilePath);

    public void ApplyPatchToIndexAndWorktree(string patchFilePath) =>
        Run("-c", "apply.whitespace=nowarn", "apply", "--index", patchFilePath);

    public void RestorePathFromHead(string relativePath) =>
        Run("checkout", "HEAD", "--", relativePath);

    public void ReadTreeEmpty() => Run("read-tree", "--empty");

    public byte[] StagedDiffRaw(string baselineTreeSha, long maxBytes) =>
        RunRaw(NoReplace("diff", "--cached", "--raw", "-z", "--abbrev=40", "--no-renames",
            "--no-ext-diff", "--no-textconv", baselineTreeSha), maxBytes);

    public HashedIngestion HashObjectNoFiltersFromStream(
        Stream source, long maxBytes, int maxInspectedPrefixBytes)
    {
        using var proc = System.Diagnostics.Process.Start(BuildGitStartInfo(
            NoReplace("hash-object", "-w", "--no-filters", "--stdin")))
            ?? throw new InvalidOperationException("failed to start git process.");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var exitTask = Task.Run(() => proc.WaitForExit());
        var inspected = new byte[Math.Min(Math.Max(maxInspectedPrefixBytes, 0), Math.Max(maxBytes, 0))];
        var inspectedFilled = 0;
        long total = 0;
        try
        {
            var stdin = proc.StandardInput.BaseStream;
            var buffer = new byte[81920];
            while (true)
            {
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                total += read;
                if (total > maxBytes)
                    throw new GitOutputLimitExceededException(maxBytes);
                var capture = Math.Min(read, inspected.Length - inspectedFilled);
                if (capture > 0)
                {
                    Buffer.BlockCopy(buffer, 0, inspected, inspectedFilled, capture);
                    inspectedFilled += capture;
                }
                stdin.Write(buffer, 0, read);
            }
            stdin.Flush();
            stdin.Close(); // EOF: git finishes hashing and emits the object id
            if (!exitTask.Wait((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
                throw new TimeoutException("git hash-object did not exit after ingesting the file.");
            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
                throw new GitException("hash-object --stdin", proc.ExitCode, stderr);
            return new HashedIngestion(stdout.Trim(), inspected, total);
        }
        catch
        {
            KillAndReap(proc);
            throw;
        }
    }

    private void ApplyPatchBytes(byte[] patchBytes, bool checkOnly)
    {
        var args = new List<string> { "-c", "apply.whitespace=nowarn" };
        args.AddRange(NoReplace("apply", "--index"));
        if (checkOnly) args.Add("--check");
        // No patch path: git apply reads the patch from standard input.
        using var proc = System.Diagnostics.Process.Start(BuildGitStartInfo([.. args]))
            ?? throw new InvalidOperationException("failed to start git process.");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var exitTask = Task.Run(() => proc.WaitForExit());
        try
        {
            var stdin = proc.StandardInput.BaseStream;
            stdin.Write(patchBytes, 0, patchBytes.Length);
            stdin.Flush();
            stdin.Close();
            if (!exitTask.Wait((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
                throw new TimeoutException("git apply timed out.");
            var stderr = stderrTask.GetAwaiter().GetResult();
            _ = stdoutTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0)
                throw new GitException("apply --index --stdin", proc.ExitCode, stderr);
        }
        catch
        {
            KillAndReap(proc);
            throw;
        }
    }

    public void VerifyPatchBytesApplyToIndexAndWorktree(byte[] patchBytes) =>
        ApplyPatchBytes(patchBytes, checkOnly: true);

    public void ApplyPatchBytesToIndexAndWorktree(byte[] patchBytes) =>
        ApplyPatchBytes(patchBytes, checkOnly: false);

    public void UpdateRefCompareAndSwap(string refName, string newSha, string expectedSha) =>
        Run("update-ref", refName, newSha, expectedSha);

    public void RestorePathFromCommit(string commitSha, string relativePath) =>
        Run("checkout", commitSha, "--", relativePath);

    private static void ValidateTrustedMessage(string message)
    {
        // Trusted message policy: non-blank, bounded, no control characters.
        if (string.IsNullOrWhiteSpace(message) || message.Length > 4096 || message.Any(char.IsControl))
            throw new InvalidOperationException(
                "the promotion commit message is missing, overlong or contains control " +
                "characters; refusing to commit.");
    }

    public string CommitIndexWithTenninetyIdentity(string message)
    {
        ValidateTrustedMessage(message);
        Run("-c", "user.name=tenninety",
            "-c", "user.email=tenninety@localhost",
            "-c", "commit.gpgsign=false",
            "commit", "--no-verify", "-m", message);
        return HeadSha();
    }

    public string CreateCommitObjectForTree(string treeSha, string parentCommitSha, string message)
    {
        ValidateTrustedMessage(message);
        return Run(NoReplace("-c", "user.name=tenninety",
            "-c", "user.email=tenninety@localhost",
            "-c", "commit.gpgsign=false",
            "commit-tree", treeSha, "-p", parentCommitSha, "-m", message)).Output.Trim();
    }

    public string ResolveCommitParent(string commitSha) =>
        Run(NoReplace("rev-parse", "--verify", $"{commitSha}^")).Output.Trim();

    public void StageAll() => Run("add", "--force", "--all");

    public string WriteTree() => Run("write-tree").Output.Trim();

    public string CommitAllowEmpty(string message)
    {
        Run("commit", "--no-verify", "--allow-empty", "-m", message);
        return HeadSha();
    }

    /// <summary>Commits only what is staged; null when the index holds no changes.</summary>
    public string? CommitStaged(string message)
    {
        if (TryRun("diff", "--cached", "--quiet").ExitCode == 0) return null;
        Run("commit", "--no-verify", "-m", message);
        return HeadSha();
    }

    private string ResolveGitPath(string gitPath)
    {
        var resolved = Run("rev-parse", "--git-path", gitPath).Output.Trim();
        return Path.IsPathRooted(resolved) ? resolved : Path.GetFullPath(Path.Combine(RepoPath, resolved));
    }

    private (int ExitCode, string Output) Run(params string[] args)
    {
        var (exitCode, output, stderr) = TryRun(args);
        if (exitCode != 0) throw new GitException(string.Join(' ', args), exitCode, stderr);
        return (exitCode, output);
    }

    private (int ExitCode, string Output, string Stderr) TryRun(params string[] args)
    {
        using var proc = System.Diagnostics.Process.Start(BuildGitStartInfo(args))
            ?? throw new InvalidOperationException("failed to start git process.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            if (!proc.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
                throw new TimeoutException(
                    $"git command timed out and did not terminate: {string.Join(' ', args)}");
            try { Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(5)); } catch { }
            throw new TimeoutException($"git command timed out: {string.Join(' ', args)}");
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (proc.ExitCode, stdout, stderr);
    }

    /// <summary>Runs git capturing RAW stdout bytes (binary-safe), with an optional hard cap.
    /// The stdout reader runs concurrently with the exit wait: a cap violation is observed
    /// IMMEDIATELY (killing and reaping the process then and there) instead of waiting for a
    /// git that is blocked writing into an undrained pipe.</summary>
    private byte[] RunRaw(string[] args, long? maxBytes)
    {
        using var proc = System.Diagnostics.Process.Start(BuildGitStartInfo(args))
            ?? throw new InvalidOperationException("failed to start git process.");
        try
        {
            var stdoutTask = Task.Run(() => ReadCapped(proc.StandardOutput.BaseStream, maxBytes));
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var exitTask = Task.Run(() => proc.WaitForExit());

            byte[] output;
            var completed = Task.WhenAny(stdoutTask, exitTask, Task.Delay(TimeSpan.FromMinutes(2)))
                .GetAwaiter().GetResult();
            if (completed == stdoutTask)
            {
                // Propagates a GitOutputLimitExceededException immediately on cap violation.
                output = stdoutTask.GetAwaiter().GetResult();
                if (!exitTask.Wait((int)TimeSpan.FromSeconds(30).TotalMilliseconds))
                    throw new TimeoutException($"git command did not exit: {string.Join(' ', args)}");
            }
            else if (completed == exitTask)
            {
                output = stdoutTask.GetAwaiter().GetResult(); // drain the remaining pipe
            }
            else
            {
                throw new TimeoutException($"git command timed out: {string.Join(' ', args)}");
            }

            var stderr = stderrTask.GetAwaiter().GetResult();
            if (proc.ExitCode != 0) throw new GitException(string.Join(' ', args), proc.ExitCode, stderr);
            return output;
        }
        catch
        {
            KillAndReap(proc);
            throw;
        }
    }

    private static void KillAndReap(System.Diagnostics.Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        try { proc.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds); } catch { }
    }

    private static byte[] ReadCapped(Stream stream, long? maxBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (maxBytes is long cap && total > cap)
                throw new GitOutputLimitExceededException(cap);
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private System.Diagnostics.ProcessStartInfo BuildGitStartInfo(string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (_isolated)
        {
            // Disposable execution profile: nothing is inherited except PATH; git sees an
            // empty home, no global/system/XDG config, no prompting and no hooks. No
            // GIT_DIR/GIT_WORK_TREE/GIT_OBJECT_DIRECTORY/credential/SSH variable or any other
            // inherited Git control variable can reach the child.
            psi.Environment.Clear();
            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(path)) psi.Environment["PATH"] = path;
            psi.Environment["HOME"] = DisposableHomeDirectory;
            psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
            psi.Environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }
        else
        {
            psi.Environment.Clear();
            foreach (var key in EnvironmentAllowlist)
            {
                var value = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(value)) psi.Environment[key] = value;
            }
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        return psi;
    }

    private void TryAbort(string operation)
    {
        try { TryRun(operation, "--abort"); } catch { }
    }
}
