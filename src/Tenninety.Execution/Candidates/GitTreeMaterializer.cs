using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Execution.Candidates;

/// <summary>Hard limits enforced DURING materialization; all fail closed, all positively
/// bounded. Deriving derived caps uses checked arithmetic.</summary>
public sealed record MaterializationLimits
{
    /// <summary>Maximum tracked file count. Enforced by the parser WHILE records are
    /// processed, before any byte is copied.</summary>
    public int MaxFiles { get; init; } = 100_000;

    /// <summary>Maximum total workspace bytes. Enforced while streaming each blob so a huge
    /// object is killed mid-stream and can never be buffered or written beyond the budget.</summary>
    public long MaxTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;

    /// <summary>Maximum UTF-8 byte length of one tree path.</summary>
    public int MaxPathBytes { get; init; } = 4096;

    /// <summary>Hard cap on the raw `ls-tree -r -z` listing before it is buffered at all.</summary>
    public long MaxTreeListingBytes { get; init; } = 64L * 1024 * 1024;

    public void Validate()
    {
        if (MaxFiles is < 1 or > 1_000_000)
            throw new InvalidOperationException(
                $"materialization MaxFiles must be within [1, 1000000] but is {MaxFiles}.");
        if (MaxTotalBytes is < 1 or > 1_099_511_627_776)
            throw new InvalidOperationException(
                $"materialization MaxTotalBytes must be within [1, 1099511627776] but is {MaxTotalBytes}.");
        if (MaxPathBytes is < 1 or > 65_536)
            throw new InvalidOperationException(
                $"materialization MaxPathBytes must be within [1, 65536] but is {MaxPathBytes}.");
        if (MaxTreeListingBytes is < 1 or > 1_073_741_824)
            throw new InvalidOperationException(
                $"materialization MaxTreeListingBytes must be within [1, 1073741824] but is {MaxTreeListingBytes}.");
    }
}

/// <summary>The exact, verified result of materializing one candidate commit.</summary>
public sealed record MaterializedTree(
    string CommitSha,
    string TreeOid,
    IReadOnlyList<GitTreeEntry> Entries);

/// <summary>
/// Trusted exact-tree exporter: materializes the full tracked content of one candidate commit
/// into a freshly created EMPTY directory. Security properties:
///
///  1. The input must be a full 40-hex commit SHA; it is resolved by trusted git
///     (<c>rev-parse --verify &lt;sha&gt;^{commit}^{tree}</c>), which also proves commit-ness
///     and yields the recorded tree OID.
///  2. The destination must already exist as a real, non-symlinked, EMPTY directory; anything
///     else fails closed before any filesystem state is produced.
///  3. Entries are enumerated with <c>ls-tree -r -z --full-tree</c> under a hard listing-byte
///     cap (derived with checked arithmetic from the limits) and parsed from raw NUL-delimited
///     bytes — the parser enforces MaxFiles and MaxPathBytes WHILE processing records.
///  4. v1 accepts only regular blobs (100644/100755); symlinks, gitlinks/submodules, trees in
///     invalid positions, unknown modes and unsafe paths fail closed
///     (<see cref="RepositoryPathPolicy"/>, <see cref="GitTreeListingParser"/>).
///  5. Blob contents are streamed by OBJECT ID (<c>cat-file blob &lt;oid&gt;</c> — never
///     <c>git show &lt;commit&gt;:&lt;path&gt;</c>) directly into their destination files
///     under the remaining total byte budget; no blob-sized allocation ever exists. A cap
///     violation kills git promptly and removes the partial file.
///  6. Destination paths are re-checked after combination: the repository-relative path is
///     recomputed and must equal the validated canonical path exactly (defense in depth).
///
/// Benchmark note (Section 22): v1 intentionally runs one `cat-file` process per blob — simple
/// and correct. A binary `cat-file --batch` parser is deliberately deferred until the
/// performance harness shows workspace materialization is material relative to end-to-end
/// model/build/test time.
/// </summary>
public sealed class GitTreeMaterializer
{
    private readonly IGitService _git;
    private readonly MaterializationLimits _limits;

    public GitTreeMaterializer(IGitService git, MaterializationLimits? limits = null)
    {
        _git = git;
        _limits = limits ?? new MaterializationLimits();
        _limits.Validate();
    }

    /// <summary>Materializes <paramref name="commitSha"/> into the empty directory
    /// <paramref name="destinationRoot"/> and returns the resolved tree with its entries.</summary>
    public MaterializedTree Materialize(string destinationRoot, string commitSha, CancellationToken ct = default)
    {
        if (!IsFullSha(commitSha))
            throw new InvalidOperationException(
                "the candidate must be a full 40-hex commit SHA resolved by trusted git code.");
        var normalizedSha = commitSha.ToLowerInvariant();

        // The destination must be a real, non-symlinked, EMPTY directory.
        if (!Directory.Exists(destinationRoot))
            throw new InvalidOperationException(
                "the materialization destination does not exist or is not a directory.");
        if (TrustedPathValidation.IsReparsePoint(destinationRoot))
            throw new InvalidOperationException(
                "the materialization destination must not be a symlink/reparse point.");
        if (Directory.EnumerateFileSystemEntries(destinationRoot).Any())
            throw new InvalidOperationException(
                "the materialization destination must be an empty directory.");

        // Resolves the tree and proves the SHA is a commit at all.
        var treeOid = _git.ResolveTreeOfCommit(normalizedSha);

        // Hard bound on the raw listing before any buffering; the per-file bound derives the
        // effective cap with checked arithmetic.
        var listingCap = Math.Min(
            _limits.MaxTreeListingBytes,
            checked((long)_limits.MaxFiles * (_limits.MaxPathBytes + 64)));
        byte[] rawListing;
        try
        {
            rawListing = _git.LsTreeRecursiveRaw(normalizedSha, listingCap);
        }
        catch (GitOutputLimitExceededException ex)
        {
            throw new InvalidOperationException(
                "the candidate tree listing exceeds the configured listing budget; " +
                "materialization fails closed before any copy.", ex);
        }

        // The parser enforces MaxFiles and MaxPathBytes WHILE processing records.
        var entries = GitTreeListingParser.Parse(rawListing, _limits.MaxFiles, _limits.MaxPathBytes);

        // Structural preflight over EVERY canonical path component, BEFORE the first blob is
        // written: cumulative directory prefixes are tracked case-insensitively together with
        // their original spelling, so case-colliding directory components (Dir/a.txt vs
        // dir/b.txt), file/directory prefix conflicts (file vs file/child, file vs
        // FILE/child) and duplicate/case-colliding leaf paths all fail closed. Paths are
        // printable ASCII (policy), so the ordinal case-insensitive form IS the canonical
        // representation.
        var directorySpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fileSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var segments = entry.Path.Split('/');
            var prefix = "";
            for (var i = 0; i < segments.Length; i++)
            {
                prefix = i == 0 ? segments[i] : $"{prefix}/{segments[i]}";
                if (i == segments.Length - 1)
                {
                    if (directorySpellings.TryGetValue(prefix, out var collidingDirectory))
                        throw new InvalidOperationException(
                            $"the candidate tree conflicts structurally: file '{entry.Path}' " +
                            $"collides with the directory '{collidingDirectory}' " +
                            "(file/directory prefix conflict); materialization fails closed.");
                    if (fileSpellings.TryGetValue(prefix, out var collidingFile))
                        throw new InvalidOperationException(
                            $"the candidate tree contains duplicate or case-colliding file " +
                            $"paths ('{collidingFile}' vs '{entry.Path}'); materialization " +
                            "fails closed.");
                    fileSpellings[prefix] = entry.Path;
                }
                else
                {
                    if (fileSpellings.TryGetValue(prefix, out var conflictingFile))
                        throw new InvalidOperationException(
                            $"the candidate tree conflicts structurally: the directory implied " +
                            $"by '{entry.Path}' collides with the file '{conflictingFile}' " +
                            "(file/directory prefix conflict); materialization fails closed.");
                    if (directorySpellings.TryGetValue(prefix, out var existingSpelling) &&
                        !string.Equals(existingSpelling, prefix, StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"the candidate tree contains case-colliding directory components " +
                            $"('{existingSpelling}' vs '{prefix}'); on a case-insensitive host " +
                            "bind the exact git tree could not be represented reliably, so " +
                            "materialization fails closed.");
                    directorySpellings[prefix] = prefix;
                }
            }
        }

        var destinationFullPath = Path.GetFullPath(destinationRoot);
        var destinationPrefix = destinationFullPath.EndsWith('/')
            ? destinationFullPath
            : destinationFullPath + '/';
        long totalBytes = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var destination = Path.GetFullPath(Path.Combine(destinationFullPath, entry.Path));
            if (!destination.StartsWith(destinationPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "a materialized path would escape the destination root; failing closed.");
            // Recompute the repository-relative path from the combined location and require
            // exact equality with the validated canonical path (paths are printable ASCII, so
            // the canonical form is the path itself).
            var recomputedRelative = Path.GetRelativePath(destinationFullPath, destination);
            if (!string.Equals(recomputedRelative, entry.Path, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "a materialized path changed when combined with the destination root; " +
                    "failing closed.");

            var remainingBudget = _limits.MaxTotalBytes - totalBytes;
            long written;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                written = _git.WriteBlobToFile(entry.ObjectSha, destination, remainingBudget);
            }
            catch (GitOutputLimitExceededException ex)
            {
                throw new InvalidOperationException(
                    "a candidate blob exceeds the remaining workspace byte budget; " +
                    "materialization fails closed before an unbounded copy.", ex);
            }
            totalBytes += written;
            ApplyFileMode(destination, entry.Mode);
        }

        return new MaterializedTree(normalizedSha, treeOid, entries);
    }

    private static bool IsFullSha(string? value) =>
        value is { Length: 40 } &&
        value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    private static void ApplyFileMode(string path, string mode)
    {
        // Deterministic modes; executables keep their executable bit. Non-Unix hosts cannot
        // represent the mode and are out of scope for container execution.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var unixMode = mode == RepositoryPathPolicy.ExecutableFileMode
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
              | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
              | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite
              | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(path, unixMode);
    }
}
