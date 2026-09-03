using System.Text;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Execution.Candidates;

/// <summary>Hard limits for the trusted extraction scan; all fail closed.</summary>
public sealed record CandidateScanLimits
{
    /// <summary>Maximum file count in the scanned workspace.</summary>
    public int MaxFiles { get; init; } = 100_000;

    /// <summary>Maximum size of one workspace file, charged from bytes actually streamed.</summary>
    public long MaxFileBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Maximum aggregate workspace bytes accepted by the scan.</summary>
    public long MaxTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Maximum UTF-8 byte length of one workspace path.</summary>
    public int MaxPathBytes { get; init; } = 4096;

    /// <summary>Hard cap on the raw baseline `ls-tree -r -z` listing before buffering.</summary>
    public long MaxTreeListingBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Hard cap on the raw NUL-delimited staged manifest before buffering.</summary>
    public long MaxStagedDiffBytes { get; init; } = 64L * 1024 * 1024;

    public void Validate()
    {
        if (MaxFiles is < 1 or > 1_000_000)
            throw new InvalidOperationException(
                $"scan MaxFiles must be within [1, 1000000] but is {MaxFiles}.");
        if (MaxFileBytes is < 1 or > 1_073_741_824)
            throw new InvalidOperationException(
                $"scan MaxFileBytes must be within [1, 1073741824] but is {MaxFileBytes}.");
        if (MaxTotalBytes is < 1 or > 1_099_511_627_776)
            throw new InvalidOperationException(
                $"scan MaxTotalBytes must be within [1, 1099511627776] but is {MaxTotalBytes}.");
        if (MaxPathBytes is < 1 or > 65_536)
            throw new InvalidOperationException(
                $"scan MaxPathBytes must be within [1, 65536] but is {MaxPathBytes}.");
        if (MaxTreeListingBytes is < 1 or > 1_073_741_824)
            throw new InvalidOperationException(
                $"scan MaxTreeListingBytes must be within [1, 1073741824] but is {MaxTreeListingBytes}.");
        if (MaxStagedDiffBytes is < 1 or > 1_073_741_824)
            throw new InvalidOperationException(
                $"scan MaxStagedDiffBytes must be within [1, 1073741824] but is {MaxStagedDiffBytes}.");
    }
}

/// <summary>One accepted regular file of the scanned workspace. <see
/// cref="ContentMayContainSecret"/> is bound to the exact ingested bytes: it was decided while
/// those bytes were streamed into the staged object, never by re-reading a mutable path.</summary>
public sealed record ScannedEntry(
    string Mode,
    string ObjectSha,
    long ByteSize,
    bool ContentMayContainSecret);

/// <summary>
/// The exact, verified result of scanning a stopped agent workspace. Construction is
/// scanner-controlled (internal); the change manifest is a frozen, deterministically sorted
/// snapshot of the parsed NUL-delimited staged diff.
/// </summary>
public sealed class CandidateScanResult
{
    internal CandidateScanResult(
        string targetTreeOid,
        IReadOnlyList<CandidateChange> changes,
        IReadOnlyDictionary<string, ScannedEntry> targetEntries)
    {
        TargetTreeOid = targetTreeOid;
        Changes = changes;
        TargetEntries = targetEntries;
    }

    public string TargetTreeOid { get; }
    /// <summary>Frozen manifest parsed from the NUL-delimited staged diff (sorted by path).</summary>
    public IReadOnlyList<CandidateChange> Changes { get; }
    public IReadOnlyDictionary<string, ScannedEntry> TargetEntries { get; }
}

/// <summary>
/// Trusted extraction scan over a STOPPED agent workspace.
///
/// Stop-before-scan rule: a CONFIRMED, identity-bound <see cref="QuiescenceProof"/> is
/// mandatory; all of its bindings (run, attempt, role, workspace attempt root) must match the
/// workspace exactly, otherwise nothing is touched. Without a valid proof nothing is scanned.
///
/// Every scan builds an EXACT target index from an EMPTY index state (`git read-tree --empty`
/// on every scan, before any file is staged) — a policy-rejected addition from an earlier
/// failed scan can never survive into a retry. The scanned state is then read back as a raw
/// NUL-delimited staged diff against the baseline tree, parsed byte-accurately, cross-checked
/// against the scanner's own target map and against `git write-tree`, and frozen into the
/// immutable change manifest.
///
/// Workspace files are read only through the Linux no-follow regular-file reader
/// (<see cref="TrustedFileReader"/>): FIFOs, sockets, devices, symlinks and non-regular
/// entries are rejected by descriptor metadata before any read; per-file and aggregate byte
/// budgets are charged from bytes actually streamed; descriptor identity/size are re-verified
/// afterwards. Git receives the opened bytes via standard input and never reopens the
/// workspace pathname. No workspace-provided program ever runs.
///
/// Exclusions are fixed and centralized: ONLY the exact disposable root `.git` directory is
/// ignored (agent tooling); any other `.git` segment — nested or case-variant, file or
/// directory — fails closed. The fixed root-relative role-transient predicate (`.aider*`,
/// `.opencode`, `.pi`) is applied before the file/directory branch so files and subtrees are
/// skipped consistently; nested or similarly named paths are NOT transient.
/// </summary>
public sealed class CandidateScanner
{
    /// <summary>
    /// Centralized fixed role-transient ROOT-component predicate. Not configurable. A
    /// workspace path is transient exactly when its FIRST path component (the workspace
    /// root name) is exactly `.opencode`, exactly `.pi`, or begins with `.aider` (ordinal,
    /// case-sensitive). The predicate is used in the two places that MUST agree: the live
    /// workspace walk (which transient roots to ignore) and the baseline seeding (which
    /// approved baseline entries to preserve under their validated baseline identity).
    /// Nested or similarly named paths are never transient.
    /// </summary>
    private static bool IsTransientRootComponent(string rootName) =>
        rootName.Equals(".opencode", StringComparison.Ordinal) ||
        rootName.Equals(".pi", StringComparison.Ordinal) ||
        rootName.StartsWith(".aider", StringComparison.Ordinal);

    private readonly IGitService _git;

    internal static Action<string>? TraceHook;

    private static void Trace(string message) => TraceHook?.Invoke(message);

    public CandidateScanner(IGitService authoritativeGit) => _git = authoritativeGit;

    public CandidateScanResult Scan(
        CandidateWorkspace workspace,
        QuiescenceProof proof,
        CandidateScanLimits? limits = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(proof);

        // Identity binding first: a proof for another run/attempt/role/workspace must be
        // rejected before the filesystem is touched at all.
        if (!string.Equals(proof.RunId, workspace.RunId, StringComparison.Ordinal) ||
            !string.Equals(proof.AttemptId, workspace.AttemptId, StringComparison.Ordinal) ||
            proof.Role != workspace.Role ||
            !string.Equals(proof.WorkspaceAttemptRoot, workspace.AttemptRoot, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the quiescence proof is not bound to this workspace (run, attempt, role or " +
                "workspace identity mismatch); nothing was scanned.");

        var scanLimits = limits ?? new CandidateScanLimits();
        scanLimits.Validate();

        // The workspace baseline must be exactly the tree of the recorded candidate commit.
        var committedTree = _git.ResolveTreeOfCommit(workspace.Revision.CommitSha);
        if (!string.Equals(committedTree, workspace.BaselineTreeOid, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the workspace baseline tree does not equal the tree of the recorded " +
                "candidate commit; extraction fails closed.");

        // The trusted ingestion location must be a real, non-symlinked repository directory
        // strictly inside the validated attempt root, isolated from the authoritative repo.
        var attemptRoot = Path.GetFullPath(workspace.AttemptRoot);
        var ingestionPath = Path.GetFullPath(workspace.TrustedIngestionPath);
        if (!ingestionPath.StartsWith(attemptRoot + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the trusted ingestion location is not inside the validated attempt root; " +
                "extraction fails closed.");
        if (TrustedPathValidation.IsReparsePoint(ingestionPath) ||
            !new DirectoryInfo(ingestionPath).Exists)
            throw new InvalidOperationException(
                "the trusted ingestion location is missing or is a symlink/reparse point; " +
                "extraction fails closed.");

        // Baseline listing from the authoritative repository (NUL-delimited, validated).
        byte[] baselineRaw;
        try
        {
            baselineRaw = _git.LsTreeRecursiveRaw(
                workspace.BaselineTreeOid, scanLimits.MaxTreeListingBytes);
        }
        catch (GitOutputLimitExceededException ex)
        {
            throw new InvalidOperationException(
                "the candidate baseline tree listing exceeds the configured scan budget; " +
                "extraction fails closed.", ex);
        }
        var baseline = GitTreeListingParser.Parse(
            baselineRaw, scanLimits.MaxFiles, scanLimits.MaxPathBytes)
            .ToDictionary(e => e.Path, e => (e.Mode, e.ObjectSha), StringComparer.Ordinal);

        // Every scan starts from an EXACT empty index: no staged entry from any earlier
        // scan — in particular a policy-rejected addition — can survive a retry.
        var ingestionGit = GitService.CreateDisposable(workspace.TrustedIngestionPath);
        if (!ingestionGit.IsRepository())
            throw new InvalidOperationException(
                "the trusted ingestion repository is missing; the workspace cannot be scanned.");
        ingestionGit.ReadTreeEmpty();

        var target = new Dictionary<string, ScannedEntry>(StringComparer.Ordinal);
        long totalBytes = 0;

        // Preserve approved baseline content under transient roots: a transient root that
        // was already tracked in the candidate keeps its EXACT baseline blob and mode in the
        // target, regardless of what the agent did to the live transient path (modified it,
        // deleted it, or replaced it with a directory/file). The live transient version is
        // never read or ingested. Preserved entries participate in the target dictionary and
        // the file-count accounting (so the target dictionary, the written tree and the raw
        // manifest cross-check stay exact); they are unchanged, so no change record and no
        // content scan exists for them.
        foreach (var (path, entry) in baseline)
        {
            if (!IsTransientRootComponent(path.Split('/')[0])) continue;
            ingestionGit.UpdateIndexCacheInfo(entry.Mode, entry.ObjectSha, path);
            target[path] = new ScannedEntry(entry.Mode, entry.ObjectSha, ByteSize: 0,
                ContentMayContainSecret: false);
        }

        // Traverse the stopped workspace and ingest accepted non-transient files into the
        // index (transient roots are skipped by the same centralized predicate).
        WalkAndIngest(ingestionGit, workspace.SourcePath, "", target, scanLimits,
            ref totalBytes, ct);

        // Read the staged state back as the authoritative NUL-delimited manifest and
        // cross-check it three ways: against the scanned target map, against a second,
        // independent base-tree-to-target-tree raw diff, and against `git write-tree`.
        byte[] stagedRaw;
        try
        {
            stagedRaw = ingestionGit.StagedDiffRaw(
                workspace.BaselineTreeOid, scanLimits.MaxStagedDiffBytes);
        }
        catch (GitOutputLimitExceededException ex)
        {
            throw new InvalidOperationException(
                "the staged change manifest exceeds the configured scan budget; extraction " +
                "fails closed.", ex);
        }
        var changes = ParseManifest(stagedRaw, baseline, target, scanLimits);

        // The written tree is captured first, then diffed against the baseline tree with a
        // SECOND, independent raw command; both parsed manifests must agree exactly.
        Trace($"post-walk targetCount={target.Count} totalBytes={totalBytes}");
        var targetTree = ingestionGit.WriteTree();
        Trace($"write-tree: {targetTree}");
        byte[] treeDiffRaw;
        try
        {
            treeDiffRaw = ingestionGit.TreeDiffNamesRaw(
                workspace.BaselineTreeOid, targetTree, scanLimits.MaxStagedDiffBytes);
        }
        catch (GitOutputLimitExceededException ex)
        {
            throw new InvalidOperationException(
                "the base-to-target tree diff exceeds the configured scan budget; " +
                "extraction fails closed.", ex);
        }
        var treeDiffChanges = ParseManifest(treeDiffRaw, baseline, target, scanLimits);
        if (changes.Count != treeDiffChanges.Count ||
            !changes.Select(c => (c.NormalizedPath, c.Kind, c.OldMode, c.NewMode, c.OldObjectHash, c.NewObjectHash))
                .SequenceEqual(treeDiffChanges.Select(c =>
                    (c.NormalizedPath, c.Kind, c.OldMode, c.NewMode, c.OldObjectHash, c.NewObjectHash))))
            throw new InvalidOperationException(
                "the staged manifest and the base-to-target tree diff disagree; extraction " +
                "fails closed.");

        return new CandidateScanResult(targetTree, changes, target);
    }

    private sealed record ManifestRecord(
        string Path, string Status, string OldMode, string NewMode,
        string OldOid, string NewOid);

    /// <summary>
    /// Parses a raw NUL-delimited diff byte-accurately: records are
    /// `:&lt;old-mode&gt; &lt;new-mode&gt; &lt;old-oid&gt; &lt;new-oid&gt; &lt;status&gt;NUL&lt;path&gt;NUL`. Only add,
    /// modify and delete statuses with supported regular modes, full hex object ids and
    /// validated ASCII paths are accepted; duplicates, malformed, truncated or inconsistent
    /// records fail closed. Every manifest entry is cross-checked against the baseline
    /// listing and the scanned target map; the result is sorted deterministically.
    /// </summary>
    private static IReadOnlyList<CandidateChange> ParseManifest(
        byte[] raw,
        IReadOnlyDictionary<string, (string Mode, string ObjectSha)> baseline,
        IReadOnlyDictionary<string, ScannedEntry> target,
        CandidateScanLimits limits)
    {
        if (raw.Length == 0) return [];
        if (raw[^1] != 0)
            throw new InvalidOperationException(
                "the change manifest is missing its terminating NUL byte; extraction fails closed.");

        var changes = new List<CandidateChange>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fieldStart = 0;
        var expectingPath = false;
        string meta = "";
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != 0) continue;
            // Reject every byte above 0x7f BEFORE decoding: ASCII decoding must never turn a
            // hostile byte into a replacement character that could smuggle a path through.
            for (var b = fieldStart; b < i; b++)
            {
                if (raw[b] <= 0x7f) continue;
                throw new InvalidOperationException(
                    "the change manifest contains a non-ASCII byte in a record; extraction " +
                    "fails closed.");
            }
            var field = Encoding.ASCII.GetString(raw, fieldStart, i - fieldStart);
            fieldStart = i + 1;
            if (!expectingPath)
            {
                if (field.Length == 0 || field[0] != ':')
                    throw new InvalidOperationException(
                        "the change manifest contains a malformed record (missing ':' " +
                        "metadata prefix); extraction fails closed.");
                meta = field;
                expectingPath = true;
                continue;
            }
            expectingPath = false;
            var path = field;

            if (!RepositoryPathPolicy.IsSafeTreePath(path))
                throw new InvalidOperationException(
                    $"a manifest path is not a safe repository-relative path " +
                    $"({Describe(path)}); extraction fails closed.");
            if (path.Length > limits.MaxPathBytes)
                throw new InvalidOperationException(
                    $"a manifest path exceeds the configured maximum path length; " +
                    "extraction fails closed.");
            if (!seen.Add(path))
                throw new InvalidOperationException(
                    $"the change manifest contains a duplicate entry for '{path}'; " +
                    "extraction fails closed.");

            var parts = meta[1..].Split(' ');
            if (parts.Length != 5)
                throw new InvalidOperationException(
                    "the change manifest metadata is malformed; extraction fails closed.");
            var (oldMode, newMode, oldOid, newOid, status) =
                (parts[0], parts[1], parts[2], parts[3], parts[4]);
            if (oldMode is not ("100644" or "100755" or "000000") ||
                newMode is not ("100644" or "100755" or "000000"))
                throw new InvalidOperationException(
                    "the change manifest carries an unsupported file mode (symlinks, " +
                    "gitlinks and non-regular entries fail closed); extraction fails closed.");
            if (!IsOidOrZero(oldOid) || !IsOidOrZero(newOid))
                throw new InvalidOperationException(
                    "the change manifest carries a malformed object id; extraction fails " +
                    "closed.");
            if (status is not ("A" or "M" or "D"))
                throw new InvalidOperationException(
                    $"the change manifest carries unsupported status '{status}' (renames, " +
                    "type changes and unmerged entries fail closed); extraction fails closed.");

            var kind = status switch
            {
                "A" => GitChangeKind.Added,
                "M" => GitChangeKind.Modified,
                _ => GitChangeKind.Deleted,
            };
            if (kind == GitChangeKind.Added)
            {
                if (oldMode != "000000" || !IsRealOid(newOid) || baseline.ContainsKey(path))
                    throw new InvalidOperationException(
                        $"the staged addition for '{path}' is inconsistent with the " +
                        "baseline; extraction fails closed.");
                if (!target.TryGetValue(path, out var scanned) ||
                    scanned.Mode != newMode || scanned.ObjectSha != newOid)
                    throw new InvalidOperationException(
                        $"the staged addition for '{path}' does not match the scanned " +
                        "workspace entry; extraction fails closed.");
                changes.Add(new CandidateChange(path, kind, "", newMode, null, newOid,
                    scanned.ByteSize));
            }
            else if (kind == GitChangeKind.Modified)
            {
                if (!IsRealOid(oldOid) || !IsRealOid(newOid) ||
                    !baseline.TryGetValue(path, out var old) ||
                    old.Mode != oldMode || old.ObjectSha != oldOid ||
                    !target.TryGetValue(path, out var scanned) ||
                    scanned.Mode != newMode || scanned.ObjectSha != newOid)
                    throw new InvalidOperationException(
                        $"the staged modification for '{path}' is inconsistent with the " +
                        "baseline or the scanned workspace entry; extraction fails closed.");
                changes.Add(new CandidateChange(path, kind, oldMode, newMode, oldOid, newOid,
                    scanned.ByteSize));
            }
            else
            {
                if (newMode != "000000" || !IsRealOid(oldOid) ||
                    !baseline.TryGetValue(path, out var old) ||
                    old.Mode != oldMode || old.ObjectSha != oldOid ||
                    target.ContainsKey(path))
                    throw new InvalidOperationException(
                        $"the staged deletion for '{path}' is inconsistent with the " +
                        "baseline; extraction fails closed.");
                changes.Add(new CandidateChange(path, kind, oldMode, "", oldOid, null, 0));
            }
        }
        if (expectingPath)
            throw new InvalidOperationException(
                "the change manifest is truncated (unterminated record); extraction fails closed.");
        return changes.OrderBy(c => c.NormalizedPath, StringComparer.Ordinal).ToList();

        static bool IsOidOrZero(string value) =>
            value.Length == 40 &&
            value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
        static bool IsRealOid(string value) =>
            IsOidOrZero(value) && !value.All(c => c == '0');
    }

    private static string Describe(string relativePath) => string.Concat(
        relativePath.Select(c => char.IsControl(c) ? $"\\x{(int)c:x2}" : c.ToString()));

    private static void WalkAndIngest(
        GitService ingestionGit,
        string directory,
        string relativePrefix,
        Dictionary<string, ScannedEntry> target,
        CandidateScanLimits limits,
        ref long totalBytes,
        CancellationToken ct)
    {
        // Iterative, bounded traversal: an explicit stack prevents deep-recursion overflow;
        // every file/dir path is validated before it is used.
        var pending = new Stack<(string Directory, string RelativePrefix)>();
        pending.Push((directory, relativePrefix));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (currentDir, currentPrefix) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(currentDir))
            {
                var name = Path.GetFileName(entry);
                var relative = currentPrefix.Length == 0 ? name : $"{currentPrefix}/{name}";

                // Centralized fixed transient predicate, applied BEFORE the file/directory
                // branch so files and directory subtrees behave consistently.
                Trace($"walk: {relative} dir={Directory.Exists(entry)}");
                if (IsTransientRootComponent(relative.Split('/')[0])) continue;

                // ONLY the exact disposable root `.git` directory is agent tooling and is
                // ignored. Every other `.git` segment — nested or case-variant, file or
                // directory — fails closed instead of disappearing silently.
                if (name.Equals(".git", StringComparison.Ordinal))
                {
                    if (currentPrefix.Length == 0 && Directory.Exists(entry))
                        continue;
                    throw new InvalidOperationException(
                        "the workspace contains a nested `.git` entry; such metadata can " +
                        "never be promoted and the whole candidate is rejected.");
                }
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    relative.Split('/').Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "the workspace contains a case-variant or nested `.git` entry; the " +
                        "whole candidate is rejected.");

                if (!RepositoryPathPolicy.IsSafeTreePath(relative) ||
                    Encoding.UTF8.GetByteCount(relative) > limits.MaxPathBytes)
                    throw new InvalidOperationException(
                        $"the workspace path {Describe(relative)} is not a safe " +
                        "repository-relative path or exceeds the configured length; " +
                        "extraction fails closed.");

                if (Directory.Exists(entry))
                {
                    if (TrustedPathValidation.IsReparsePoint(entry))
                        throw new InvalidOperationException(
                            $"the workspace contains a symlink/reparse point at {Describe(relative)}; " +
                            "extraction fails closed.");
                    pending.Push((entry, relative));
                    continue;
                }

                // Everything that is not a directory is ingested through the no-follow
                // regular-file reader, which rejects symlinks, FIFOs, sockets and devices by
                // descriptor metadata without ever blocking on or reopening the path.
                IngestFile(ingestionGit, entry, relative, target, limits, ref totalBytes);
            }
        }
    }

    private static void IngestFile(
        GitService ingestionGit,
        string path,
        string relative,
        Dictionary<string, ScannedEntry> target,
        CandidateScanLimits limits,
        ref long totalBytes)
    {
        if (target.Count >= limits.MaxFiles)
            throw new InvalidOperationException(
                $"the workspace exceeds the configured maximum of {limits.MaxFiles} files; " +
                "extraction fails closed.");

        // No-follow open with descriptor-metadata proof of a regular file; mode and size come
        // from the opened descriptor, never from a pathname lookup.
        using var opened = TrustedFileReader.OpenRegularFileNoFollow(path);
        if (opened.Length > limits.MaxFileBytes)
            throw new InvalidOperationException(
                $"the workspace file {Describe(relative)} is {opened.Length} bytes, exceeding " +
                $"the configured per-file maximum of {limits.MaxFileBytes}; extraction fails " +
                "closed.");
        if (totalBytes + opened.Length > limits.MaxTotalBytes)
            throw new InvalidOperationException(
                "the workspace exceeds the configured aggregate byte budget; extraction " +
                "fails closed.");

        using var stream = new FileStream(
            opened.Handle, FileAccess.Read, bufferSize: 81920, isAsync: false);
        // The pump charges limits from the bytes actually streamed and captures the bounded
        // prefix of the EXACT ingested bytes for the promotion policy's content scan.
        var remainingAggregate = limits.MaxTotalBytes - totalBytes;
        var ingestion = ingestionGit.HashObjectNoFiltersFromStream(
            stream,
            Math.Min(limits.MaxFileBytes, remainingAggregate),
            PromotionPolicy.MaxContentScanBytesPerFile);
        opened.VerifyUnchanged(ingestion.BytesRead);

        var mode = opened.Executable ? "100755" : "100644";
        if (ingestion.ObjectSha.Length != 40)
            throw new InvalidOperationException(
                "the ingestion repository returned a malformed object id; extraction fails " +
                "closed.");
        ingestionGit.UpdateIndexCacheInfo(mode, ingestion.ObjectSha, relative);
        totalBytes += ingestion.BytesRead;
        target[relative] = new ScannedEntry(
            mode, ingestion.ObjectSha, ingestion.BytesRead,
            ContentMayContainSecret: PromotionPolicy.ContainsLikelySecret(
                Encoding.UTF8.GetString(ingestion.InspectedPrefix)));
        Trace($"ingested: {relative} mode={mode} oid={ingestion.ObjectSha} count={target.Count}");
}
}
