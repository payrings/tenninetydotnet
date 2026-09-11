using System.Text;
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
    /// <summary>Top-level working-tree directory of the repository CONTAINING RepoPath, or
    /// null when not inside a work tree (linked worktrees and subdirectories included).</summary>
    string? RepositoryTopLevel();
    /// <summary>True when RepoPath refers to a bare repository.</summary>
    bool IsBareRepository();
    /// <summary>Establishes the repository-LOCAL Tenninety commit identity when no identity is
    /// resolvable; never touches global or system configuration.</summary>
    void EnsureLocalIdentity();
    void Init();
    bool IsClean();
    bool IsPathClean(string relativePath);
    /// <summary>True when no tracked, staged, or untracked changes exist except the named
    /// repository-relative path.</summary>
    bool IsCleanExcept(string relativePath) => false;
    /// <summary>True when the commit changes exactly one expected repository-relative path.</summary>
    bool CommitChangesOnlyPath(string commitSha, string expectedPath) => false;
    bool IsPathIgnored(string relativePath) => false;
    IReadOnlyList<string> WorktreePaths() => [RepoPath];
    string CurrentBranch();
    /// <summary>Branch HEAD symbolically points at, or null when detached; also valid on an
    /// unborn branch (no commit yet).</summary>
    string? SymbolicHeadBranch();
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
    /// <summary>Creates and verifies the exact promotion object BEFORE changing any ref or
    /// checkout, honoring repository identity and commit.gpgsign. Requires the clean checked-out
    /// work branch at expectedCandidateSha, descending from main at expectedBaseSha. Persist
    /// the returned SHA with these inputs before calling CompleteSquashPromotion.</summary>
    string PrepareSquashPromotion(string branch, string expectedBaseSha, string expectedCandidateSha,
        string message) => throw new NotSupportedException("squash promotion preparation is not implemented.");
    /// <summary>Publishes the prepared object using CAS, then completes the checkout without
    /// deleting the work branch. Repeatable with the SAME inputs after interruption, including
    /// when main already equals promotionSha. Never rewinds main; ambiguous state is preserved.</summary>
    void CompleteSquashPromotion(string branch, string expectedBaseSha, string expectedCandidateSha,
        string promotionSha) => throw new NotSupportedException("squash promotion completion is not implemented.");
    /// <summary>Removes the internal refs retaining the prepared promotion and candidate
    /// objects. Call only after durable transaction evidence has been removed.</summary>
    void ReleaseSquashPromotion(string expectedCandidateSha, string promotionSha) { }
    /// <summary>Deletes the branch only if it still identifies the exact expected candidate.</summary>
    void DeleteBranchCompareAndSwap(string branch, string expectedSha) =>
        throw new NotSupportedException("compare-and-swap branch deletion is not implemented.");
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

/// <summary>A publication/checkout failure. The caller must retain its durable promotion
/// record, even if forward recovery succeeded. A failed recovery quarantines the preserved
/// repository for operator inspection; neither failure is discarded.</summary>
public sealed class SquashPromotionException : InvalidOperationException
{
    public string PromotionSha { get; }
    public Exception OriginalException { get; }
    public Exception? RecoveryException { get; }
    public bool RecoverySucceeded => RecoveryException is null;

    internal SquashPromotionException(string sha, Exception original, Exception? recovery)
        : base(recovery is null
            ? $"squash promotion {sha} failed: {original.Message}; forward recovery completed. " +
              "Retain the promotion record and repeat completion before cleanup."
            : $"squash promotion {sha} failed: {original.Message}; recovery failed: {recovery.Message}. " +
              "Repository quarantined, unsafe for retry; refs, index and worktree were preserved.",
            recovery is null ? original : new AggregateException(original, recovery))
    {
        PromotionSha = sha;
        OriginalException = original;
        RecoveryException = recovery;
    }
}

internal enum SquashPromotionPhase
{
    AfterCommitObject,
    BeforeMainCompareAndSwap,
    AfterMainCompareAndSwap,
    BeforeCheckout,
    AfterCheckout,
    BeforeRecovery,
}

public sealed class GitService : IGitService
{
    private const string DiffTruncationMarker =
        "\n… [diff truncated – showing head and tail] …\n";
    private const string StderrTruncationMarker =
        "\n... [git stderr truncated - showing head and tail] ...\n";
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

    internal Action<SquashPromotionPhase>? SquashPromotionFailpoint { get; set; }

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

    /// <summary>Absolute top-level working-tree directory of the repository that CONTAINS
    /// RepoPath, or null when RepoPath is not inside a work tree. Uses
    /// `git rev-parse --show-toplevel` so linked worktrees and subdirectories are detected
    /// exactly like Git sees them.</summary>
    public string? RepositoryTopLevel()
    {
        var result = TryRun("rev-parse", "--show-toplevel");
        return result.ExitCode == 0 && result.Output.Trim().Length > 0
            ? result.Output.Trim()
            : null;
    }

    /// <summary>True when RepoPath refers to a bare repository (no work tree).</summary>
    public bool IsBareRepository() =>
        TryRun("rev-parse", "--is-bare-repository").Output.Trim() == "true";

    /// <summary>Ensures a commit identity is resolvable in this repository by establishing the
    /// repository-LOCAL Tenninety identity when none exists — exactly like <see cref="Init"/>.
    /// Never touches global or system configuration.</summary>
    public void EnsureLocalIdentity()
    {
        if (HasIdentity()) return;
        Run("config", "user.name", "tenninety");
        Run("config", "user.email", "tenninety@localhost");
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
        EnsureLocalIdentity();
    }

    private bool HasIdentity() =>
        TryRun("config", "user.name").ExitCode == 0 && TryRun("config", "user.email").ExitCode == 0;

    /// <summary>Test seam (InternalsVisibleTo): runs an arbitrary git command verbatim.</summary>
    internal void RunForTest(params string[] args) => Run(args);

    /// <summary>Test seam (InternalsVisibleTo): reads a repository-LOCAL config value, or
    /// null when unset. Global/system configuration is never consulted.</summary>
    internal string? ShowLocalConfigForTest(string key)
    {
        var result = TryRun("config", "--local", "--get", key);
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    public bool IsClean() => Run("status", "--porcelain").Output.Trim().Length == 0;

    public bool IsPathClean(string relativePath) =>
        Run("status", "--porcelain", "--", relativePath).Output.Trim().Length == 0;

    public bool IsCleanExcept(string relativePath) =>
        Run("status", "--porcelain=v1", "-z", "--untracked-files=all", "--", ".",
            $":(exclude){relativePath.Replace('\\', '/')}").Output.Length == 0;

    public bool CommitChangesOnlyPath(string commitSha, string expectedPath)
    {
        var paths = Run(
                "--no-replace-objects", "diff-tree", "--no-commit-id", "--name-only",
                "-r", "-z", "--no-renames", commitSha)
            .Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return paths.Length == 1 &&
               paths[0].Equals(expectedPath.Replace('\\', '/'), StringComparison.Ordinal);
    }

    public bool IsPathIgnored(string relativePath) =>
        TryRun("check-ignore", "--quiet", "--", relativePath).ExitCode == 0;

    public IReadOnlyList<string> WorktreePaths() =>
        Run("worktree", "list", "--porcelain", "-z").Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(entry => Path.GetFullPath(entry[9..]))
            .ToArray();

    public string CurrentBranch() => Run("rev-parse", "--abbrev-ref", "HEAD").Output.Trim();

    /// <summary>Branch name HEAD symbolically points at, or null when HEAD is detached. Unlike
    /// <see cref="CurrentBranch"/> this also works on an unborn branch (a repository without
    /// any commit yet).</summary>
    public string? SymbolicHeadBranch()
    {
        var result = TryRun("symbolic-ref", "--short", "--quiet", "HEAD");
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

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
        var baseSha = PromotionRef(TenNinety.MainBranch);
        var candidateSha = PromotionRef(branch);
        var sha = PrepareSquashPromotion(branch, baseSha, candidateSha, message);
        var completed = false;
        try
        {
            CompleteSquashPromotion(branch, baseSha, candidateSha, sha);
            completed = true;
        }
        catch (SquashPromotionException ex) when (ex.RecoverySucceeded)
        {
            // The exact prepared commit is published and the checkout was recovered. Callers
            // without a durable journal (legacy API) can safely treat this as completed.
            completed = true;
        }
        finally
        {
            if (completed) ReleaseSquashPromotion(candidateSha, sha);
        }
        return sha;
    }

    public string PrepareSquashPromotion(string branch, string expectedBaseSha,
        string expectedCandidateSha, string message)
    {
        ValidateTrustedMessage(message);
        ValidateSquashInputs(branch, expectedBaseSha, expectedCandidateSha, requireBranch: true);
        RequireCleanPromotionCheckout(branch, expectedBaseSha, expectedCandidateSha);
        var tree = CreateSquashTree(expectedBaseSha, expectedCandidateSha);
        var sign = PromotionSigningEnabled();
        var sha = Run(NoReplace("commit-tree", tree, "-p", expectedBaseSha,
            sign ? "-S" : "--no-gpg-sign", "-m", message)).Output.Trim();
        SquashPromotionFailpoint?.Invoke(SquashPromotionPhase.AfterCommitObject);
        VerifySquashObject(sha, expectedBaseSha, tree, requireSignature: sign);
        ValidateSquashInputs(branch, expectedBaseSha, expectedCandidateSha, requireBranch: true);
        RequireCleanPromotionCheckout(branch, expectedBaseSha, expectedCandidateSha);
        if (PromotionSigningEnabled() != sign)
            throw new InvalidOperationException("commit.gpgsign changed while preparing the promotion.");
        AnchorSquashPromotion(expectedCandidateSha, sha);
        return sha;
    }

    public void CompleteSquashPromotion(string branch, string expectedBaseSha,
        string expectedCandidateSha, string promotionSha)
    {
        var main = PromotionRef(TenNinety.MainBranch);
        var candidateRef = PromotionRefOrNull(branch);
        var branchExists = candidateRef is not null;
        if (candidateRef is not null && candidateRef != expectedCandidateSha)
            throw new InvalidOperationException(
                "the candidate branch changed during squash promotion; it was preserved for inspection.");
        if (!branchExists && main != promotionSha)
            throw new InvalidOperationException(
                "the candidate branch is absent before the exact promotion was published.");
        ValidateSquashInputs(branch, expectedBaseSha, expectedCandidateSha, requireBranch: branchExists);
        var tree = CreateSquashTree(expectedBaseSha, expectedCandidateSha);
        VerifySquashObject(
            promotionSha, expectedBaseSha, tree,
            requireSignature: PromotionSigningEnabled());
        var published = main == promotionSha;
        try
        {
            if (!published)
            {
                if (main != expectedBaseSha)
                    throw new InvalidOperationException(
                        "main changed after promotion preparation; no history was rewritten.");
                RequireCleanPromotionCheckout(branch, expectedBaseSha, expectedCandidateSha);
                SquashPromotionFailpoint?.Invoke(SquashPromotionPhase.BeforeMainCompareAndSwap);
                UpdateMainAndVerifyCandidate(
                    branch, expectedBaseSha, expectedCandidateSha, promotionSha);
                published = true;
                SquashPromotionFailpoint?.Invoke(SquashPromotionPhase.AfterMainCompareAndSwap);
            }
            FinishCheckout(recovering: false);
        }
        catch (Exception original)
        {
            // A Git process can publish and then fail to report success. Inspect exact refs
            // in recovery instead of ever undoing a possibly published commit.
            if (!published)
            {
                try { published = PromotionRef(TenNinety.MainBranch) == promotionSha; }
                catch (Exception recovery) { throw new SquashPromotionException(promotionSha, original, recovery); }
            }
            if (!published) throw;
            try
            {
                SquashPromotionFailpoint?.Invoke(SquashPromotionPhase.BeforeRecovery);
                FinishCheckout(recovering: true);
            }
            catch (Exception recovery)
            {
                throw new SquashPromotionException(promotionSha, original, recovery);
            }
            throw new SquashPromotionException(promotionSha, original, null);
        }

        void FinishCheckout(bool recovering)
        {
            if (PromotionRef(TenNinety.MainBranch) != promotionSha)
                throw new InvalidOperationException(
                    "main no longer identifies the prepared promotion; repository quarantined.");
            var currentCandidate = PromotionRefOrNull(branch);
            if (currentCandidate is not null && currentCandidate != expectedCandidateSha)
                throw new InvalidOperationException(
                    "the candidate branch changed after publication; repository quarantined.");

            var headBranch = SymbolicHeadBranch();
            if (headBranch == branch)
            {
                RequireIndexAndWorktree(expectedCandidateSha);
                if (!recovering) SquashPromotionFailpoint?.Invoke(SquashPromotionPhase.BeforeCheckout);
                Run("checkout", "--no-overwrite-ignore", TenNinety.MainBranch);
            }
            else if (headBranch == TenNinety.MainBranch)
            {
                var indexTree = WriteTree();
                var promotionTree = ResolveTreeOfCommit(promotionSha);
                if (indexTree == promotionTree)
                {
                    RequireWorktreeMatchesIndex();
                }
                else if (indexTree == ResolveTreeOfCommit(expectedBaseSha))
                {
                    // A process can die immediately after the main CAS, leaving HEAD on the
                    // new commit while index/worktree still exactly match the recorded base.
                    // That exact state is operation-owned. Any other partial state is retained.
                    RequireWorktreeMatchesIndex();
                    if (!recovering) SquashPromotionFailpoint?.Invoke(SquashPromotionPhase.BeforeCheckout);
                    Run("read-tree", "-m", "-u", promotionSha);
                }
                else
                    throw new InvalidOperationException(
                        "the main index is neither the recorded base nor promotion tree; " +
                        "external or partial changes were preserved.");
            }
            else
                throw new InvalidOperationException(
                    "HEAD is on an unrelated branch or detached; it was preserved for inspection.");

            if (!recovering) SquashPromotionFailpoint?.Invoke(SquashPromotionPhase.AfterCheckout);
            if (SymbolicHeadBranch() != TenNinety.MainBranch || HeadSha() != promotionSha ||
                WriteTree() != ResolveTreeOfCommit(promotionSha))
                throw new InvalidOperationException(
                    "the promotion checkout did not reach the exact prepared commit.");
            RequireWorktreeMatchesIndex();
        }
    }

    public void DeleteBranchCompareAndSwap(string branch, string expectedSha)
    {
        Run("check-ref-format", $"refs/heads/{branch}");
        RequirePromotionOid(expectedSha);
        var current = PromotionRefOrNull(branch);
        if (current is null) return;
        if (current != expectedSha)
            throw new InvalidOperationException(
                $"branch '{branch}' changed after promotion; it was not deleted.");
        if (SymbolicHeadBranch() == branch)
            throw new InvalidOperationException(
                $"branch '{branch}' is still checked out; it was not deleted.");
        if (Run("worktree", "list", "--porcelain", "-z").Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Any(entry => entry == $"branch refs/heads/{branch}"))
            throw new InvalidOperationException(
                $"branch '{branch}' is checked out in another worktree; it was not deleted.");
        Run("update-ref", "-d", $"refs/heads/{branch}", expectedSha);
        if (PromotionRefOrNull(branch) is not null)
            throw new InvalidOperationException(
                $"branch '{branch}' deletion could not be proven.");
    }

    public void ReleaseSquashPromotion(string expectedCandidateSha, string promotionSha)
    {
        RequirePromotionOid(expectedCandidateSha);
        RequirePromotionOid(promotionSha);
        var promotionRef = PromotionAnchorRef("prepared", promotionSha);
        var candidateRef = PromotionAnchorRef("candidates", promotionSha);
        var currentPromotion = ExactRefOrNull(promotionRef);
        var currentCandidate = ExactRefOrNull(candidateRef);
        if (currentPromotion is not null && currentPromotion != promotionSha)
            throw new InvalidOperationException("the internal promotion retention ref changed; it was preserved.");
        if (currentCandidate is not null && currentCandidate != expectedCandidateSha)
            throw new InvalidOperationException("the internal candidate retention ref changed; it was preserved.");
        if (currentPromotion is null && currentCandidate is null) return;

        var commands = new StringBuilder("start\n");
        if (currentPromotion is not null)
            commands.Append($"delete {promotionRef} {promotionSha}\n");
        if (currentCandidate is not null)
            commands.Append($"delete {candidateRef} {expectedCandidateSha}\n");
        commands.Append("prepare\ncommit\n");
        RunWithInput(["update-ref", "--stdin"], commands.ToString());
        if (ExactRefOrNull(promotionRef) is not null || ExactRefOrNull(candidateRef) is not null)
            throw new InvalidOperationException("internal promotion retention refs could not be removed.");
    }

    private void ValidateSquashInputs(
        string branch, string baseSha, string candidateSha, bool requireBranch)
    {
        Run("check-ref-format", $"refs/heads/{branch}");
        if (branch == TenNinety.MainBranch)
            throw new InvalidOperationException("the squash work branch must not be main.");
        RequirePromotionOid(baseSha);
        RequirePromotionOid(candidateSha);
        RequireCommitObject(baseSha);
        RequireCommitObject(candidateSha);
        if (requireBranch && PromotionRef(branch) != candidateSha)
            throw new InvalidOperationException("the candidate branch no longer identifies the reviewed commit.");
    }

    private static void RequirePromotionOid(string sha)
    {
        if (sha.Length is not (40 or 64) || !sha.All(Uri.IsHexDigit))
            throw new InvalidOperationException("promotion requires exact full commit object IDs.");
    }

    private string PromotionRef(string branch)
    {
        Run("check-ref-format", $"refs/heads/{branch}");
        if (TryRun("symbolic-ref", "--quiet", $"refs/heads/{branch}").ExitCode == 0)
            throw new InvalidOperationException($"promotion branch '{branch}' must not be a symbolic ref.");
        return Run(NoReplace("rev-parse", "--verify", $"refs/heads/{branch}")).Output.Trim();
    }

    private string? PromotionRefOrNull(string branch)
    {
        Run("check-ref-format", $"refs/heads/{branch}");
        if (TryRun("symbolic-ref", "--quiet", $"refs/heads/{branch}").ExitCode == 0)
            throw new InvalidOperationException($"promotion branch '{branch}' must not be a symbolic ref.");
        var result = TryRun(NoReplace("rev-parse", "--verify", $"refs/heads/{branch}"));
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private string CreateSquashTree(string baseSha, string candidateSha)
    {
        var result = TryRun(NoReplace("merge-tree", "--write-tree", baseSha, candidateSha));
        if (result.ExitCode != 0)
            throw new GitException(
                $"merge-tree --write-tree {baseSha} {candidateSha}", result.ExitCode, result.Stderr);
        var tree = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        RequirePromotionOid(tree);
        if (tree == ResolveTreeOfCommit(baseSha))
            throw new InvalidOperationException("squash promotion produced no changes.");
        return tree;
    }

    private bool PromotionSigningEnabled()
    {
        var result = TryRun("config", "--type=bool", "--get", "commit.gpgsign");
        if (result.ExitCode == 1) return false;
        if (result.ExitCode != 0)
            throw new GitException("config --type=bool --get commit.gpgsign", result.ExitCode, result.Stderr);
        return result.Output.Trim() == "true";
    }

    private void VerifySquashObject(
        string sha, string baseSha, string tree, bool requireSignature)
    {
        RequirePromotionOid(sha);
        RequireCommitObject(sha);
        // Inspect raw headers, not revision walking: grafts/replacements must not hide an
        // extra parent, and signature continuation lines are not commit headers.
        var headers = Run(NoReplace("cat-file", "commit", sha)).Output.Split("\n\n", 2)[0].Split('\n');
        if (!headers.Where(l => l.StartsWith("parent ", StringComparison.Ordinal))
                .SequenceEqual([$"parent {baseSha}"]) ||
            !headers.Where(l => l.StartsWith("tree ", StringComparison.Ordinal))
                .SequenceEqual([$"tree {tree}"]))
            throw new InvalidOperationException("promotion commit does not have the exact candidate tree and single base parent.");
        if (requireSignature && !headers.Any(l => l.StartsWith("gpgsig ", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "commit.gpgsign requires a signed promotion object; refusing publication.");
    }

    private void AnchorSquashPromotion(string candidateSha, string promotionSha)
    {
        var promotionRef = PromotionAnchorRef("prepared", promotionSha);
        var candidateRef = PromotionAnchorRef("candidates", promotionSha);
        var currentPromotion = ExactRefOrNull(promotionRef);
        var currentCandidate = ExactRefOrNull(candidateRef);
        if (currentPromotion == promotionSha && currentCandidate == candidateSha) return;
        if (currentPromotion is not null || currentCandidate is not null)
            throw new InvalidOperationException(
                "internal promotion retention refs conflict with the prepared objects.");

        var zero = new string('0', promotionSha.Length);
        var commands = "start\n" +
                       $"update {promotionRef} {promotionSha} {zero}\n" +
                       $"update {candidateRef} {candidateSha} {zero}\n" +
                       "prepare\ncommit\n";
        RunWithInput(["update-ref", "--stdin"], commands);
        if (ExactRefOrNull(promotionRef) != promotionSha ||
            ExactRefOrNull(candidateRef) != candidateSha)
            throw new InvalidOperationException(
                "prepared promotion objects could not be retained for crash recovery.");
    }

    private static string PromotionAnchorRef(string kind, string promotionSha) =>
        $"refs/tenninety/{kind}/{promotionSha}";

    private string? ExactRefOrNull(string refName)
    {
        if (TryRun("symbolic-ref", "--quiet", refName).ExitCode == 0)
            throw new InvalidOperationException(
                $"internal promotion ref '{refName}' must not be symbolic.");
        var result = TryRun(NoReplace("rev-parse", "--verify", refName));
        return result.ExitCode == 0 ? result.Output.Trim() : null;
    }

    private void RequireCleanPromotionCheckout(string branch, string baseSha, string candidateSha)
    {
        if (PromotionRef(TenNinety.MainBranch) != baseSha || PromotionRef(branch) != candidateSha)
            throw new InvalidOperationException("main or candidate ref changed during squash promotion.");
        var headBranch = SymbolicHeadBranch();
        if (headBranch != branch && headBranch != TenNinety.MainBranch)
            throw new InvalidOperationException("HEAD branch changed; promotion requires its work branch or the completed main checkout.");
        var expectedHead = headBranch == branch ? candidateSha : baseSha;
        if (HeadSha() != expectedHead)
            throw new InvalidOperationException("HEAD commit changed during squash promotion.");
        RequireIndexAndWorktree(expectedHead);
        var topLevel = RepositoryTopLevel();
        string? worktree = null;
        foreach (var entry in Run("worktree", "list", "--porcelain", "-z").Output.Split('\0'))
        {
            if (entry.StartsWith("worktree ", StringComparison.Ordinal)) worktree = entry[9..];
            if (entry == $"branch refs/heads/{TenNinety.MainBranch}" && worktree != topLevel)
                throw new InvalidOperationException("main is checked out in another worktree; refusing to change its ref.");
        }
    }

    private void RequireIndexAndWorktree(string expectedCommit)
    {
        var entries = Run("ls-files", "-v", "-z").Output
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Any(entry => entry[0] != 'H') ||
            WriteTree() != ResolveTreeOfCommit(expectedCommit))
            throw new InvalidOperationException(
                "promotion index does not exactly match the recorded checkout; it was preserved.");
        RequireWorktreeMatchesIndex();
    }

    private void RequireWorktreeMatchesIndex()
    {
        var worktree = TryRun("diff", "--quiet", "--ignore-submodules=none", "--");
        if (worktree.ExitCode is not 0 and not 1)
            throw new GitException("diff --quiet --ignore-submodules=none", worktree.ExitCode, worktree.Stderr);
        if (worktree.ExitCode != 0 ||
            Run("ls-files", "--others", "--exclude-standard", "-z").Output.Length != 0)
            throw new InvalidOperationException(
                "promotion worktree contains external or partial changes; they were preserved.");
    }

    private void UpdateMainAndVerifyCandidate(
        string branch, string baseSha, string candidateSha, string promotionSha)
    {
        var commands = "start\n" +
                       $"update refs/heads/{TenNinety.MainBranch} {promotionSha} {baseSha}\n" +
                       $"verify refs/heads/{branch} {candidateSha}\n" +
                       "prepare\ncommit\n";
        RunWithInput(["update-ref", "--stdin"], commands);
    }

    private void RunWithInput(string[] args, string input)
    {
        using var proc = System.Diagnostics.Process.Start(BuildGitStartInfo(args))
            ?? throw new InvalidOperationException("failed to start git process.");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.StandardInput.Write(input);
        proc.StandardInput.Close();
        if (!proc.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            KillAndReap(proc);
            throw new TimeoutException($"git command timed out: {string.Join(' ', args)}");
        }
        _ = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new GitException(string.Join(' ', args), proc.ExitCode, error);
    }

    public string HeadSha() => Run("rev-parse", "HEAD").Output.Trim();

    public string DiffHeadStat() => Run("diff", "HEAD", "--stat").Output;

    public string DiffAgainstMain(string branch) =>
        Run("diff", $"{TenNinety.MainBranch}...{branch}", "--stat").Output;

    /// <summary>Bounded unified patch of branch vs main; stdout and stderr are drained
    /// concurrently while only bounded head/tail text is retained. Long patches keep an
    /// elision marker so a model sees both the opening context and the latest changes.</summary>
    public string DiffPatchAgainstMain(string branch, int maxChars = 20000)
    {
        if (maxChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars), "the diff character limit must be positive.");
        var args = new[] { "diff", $"{TenNinety.MainBranch}...{branch}" };
        var (exitCode, patch, stderr) = TryRunBoundedText(args, maxChars);
        if (exitCode != 0)
            throw new GitException(string.Join(' ', args), exitCode, stderr);
        return patch;
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
        var r = TryRun("show", "-s", "--pretty=format:%H%x1f%s%x1f%an%x1f%aI",
            "--end-of-options", shaOrRef);
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

    private (int ExitCode, string Output, string Stderr) TryRunBoundedText(
        string[] args, int maxOutputChars)
    {
        using var proc = System.Diagnostics.Process.Start(BuildGitStartInfo(args))
            ?? throw new InvalidOperationException("failed to start git process.");
        var stdoutTask = Task.Run(() => ReadHeadTail(
            proc.StandardOutput, maxOutputChars, DiffTruncationMarker));
        var stderrTask = Task.Run(() => ReadHeadTail(
            proc.StandardError, 16_384, StderrTruncationMarker));
        try
        {
            if (!proc.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (!proc.WaitForExit((int)TimeSpan.FromSeconds(10).TotalMilliseconds))
                    throw new TimeoutException(
                        $"git command timed out and did not terminate: {string.Join(' ', args)}");
                try { Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(5)); } catch { }
                throw new TimeoutException($"git command timed out: {string.Join(' ', args)}");
            }
            return (
                proc.ExitCode,
                stdoutTask.GetAwaiter().GetResult(),
                stderrTask.GetAwaiter().GetResult());
        }
        catch
        {
            KillAndReap(proc);
            try { Task.WaitAll([stdoutTask, stderrTask], TimeSpan.FromSeconds(5)); } catch { }
            throw;
        }
    }

    private static string ReadHeadTail(TextReader reader, int maxChars, string marker)
    {
        var headLimit = (int)((long)maxChars * 3 / 4);
        var tailLimit = maxChars - headLimit;
        var head = new StringBuilder(Math.Min(headLimit, 4096));
        var tail = new char[tailLimit];
        var tailCount = 0;
        var tailWrite = 0;
        long total = 0;
        var buffer = new char[4096];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            var offset = 0;
            var headChars = Math.Min(read, headLimit - head.Length);
            if (headChars > 0)
            {
                head.Append(buffer, 0, headChars);
                offset = headChars;
            }
            for (; offset < read; offset++)
            {
                tail[tailWrite] = buffer[offset];
                tailWrite = (tailWrite + 1) % tailLimit;
                if (tailCount < tailLimit) tailCount++;
            }
        }

        var tailText = TailText(tail, tailCount, tailWrite);
        if (total <= maxChars) return head + tailText;

        var headText = head.ToString();
        // Do not split a valid UTF-16 surrogate pair at either elision boundary.
        if (headText.Length > 0 && char.IsHighSurrogate(headText[^1]))
            headText = headText[..^1];
        if (tailText.Length > 0 && char.IsLowSurrogate(tailText[0]))
            tailText = tailText[1..];
        return headText + marker + tailText;
    }

    private static string TailText(char[] tail, int count, int write)
    {
        if (count == 0) return "";
        if (count < tail.Length) return new string(tail, 0, count);
        var ordered = new char[count];
        var first = tail.Length - write;
        Array.Copy(tail, write, ordered, 0, first);
        if (write > 0) Array.Copy(tail, 0, ordered, first, write);
        return new string(ordered);
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
            StandardOutputEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
            StandardErrorEncoding = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
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
