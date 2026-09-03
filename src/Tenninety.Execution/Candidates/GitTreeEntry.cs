using System.Text;

namespace Tenninety.Execution.Candidates;

/// <summary>One entry of a recursive git tree listing (mode, object type, object ID, path).
/// Only entries the v1 materializer can safely handle ever leave the parser.</summary>
public sealed record GitTreeEntry(string Mode, string ObjectType, string ObjectSha, string Path);

/// <summary>
/// Parses raw `git ls-tree -r -z --full-tree` output. The -z form is NUL-delimited and never
/// quotes paths, so parsing operates on exact bytes — never on quoted/human display output.
/// Structural rules: non-empty output MUST terminate with a NUL byte (the zero-byte output is
/// the only valid empty-tree representation); consecutive NUL bytes (empty records) and
/// trailing bytes after the terminating NUL are rejected. Every record must be
/// `mode SP type SP oid TAB path`: the mode must be a supported regular-file mode, the type
/// must be `blob`, the object ID a full hex SHA, the path must decode as strict UTF-8, fit the
/// configured maximum UTF-8 byte length, and pass <see cref="RepositoryPathPolicy.IsSafeTreePath"/>.
/// The configured maximum file count is enforced WHILE records are processed — an oversized
/// listing fails closed without ever materializing an unrestricted list. Anything else fails
/// closed.
/// </summary>
public static class GitTreeListingParser
{
    private const byte Tab = 0x09;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static IReadOnlyList<GitTreeEntry> Parse(byte[] raw, int maxFiles, int maxPathBytes)
    {
        if (maxFiles < 1)
            throw new InvalidOperationException(
                $"the maximum tracked file count must be positive but is {maxFiles}.");
        if (maxPathBytes < 1)
            throw new InvalidOperationException(
                $"the maximum UTF-8 path length must be positive but is {maxPathBytes}.");
        // The zero-byte output is the only valid empty-tree representation.
        if (raw.Length == 0) return [];
        if (raw[^1] != 0)
            throw new InvalidOperationException(
                "git tree listing is missing its terminating NUL byte; failing closed.");

        var entries = new List<GitTreeEntry>();
        var start = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != 0) continue;
            if (i == start)
                throw new InvalidOperationException(
                    "git tree listing contains an empty record (consecutive NUL bytes); " +
                    "failing closed.");
            entries.Add(ParseRecord(raw, start, i - start, maxPathBytes));
            if (entries.Count > maxFiles)
                throw new InvalidOperationException(
                    $"the candidate tree exceeds the configured maximum of {maxFiles} " +
                    "tracked files; materialization fails closed.");
            start = i + 1;
        }
        if (start < raw.Length)
            throw new InvalidOperationException(
                "git tree listing has trailing bytes after the terminating NUL; failing closed.");
        return entries;
    }

    private static GitTreeEntry ParseRecord(byte[] raw, int start, int length, int maxPathBytes)
    {
        var record = raw.AsSpan(start, length);
        var tab = record.IndexOf(Tab);
        if (tab < 0)
            throw new InvalidOperationException(
                "git ls-tree record is malformed: the metadata/path separator is missing.");
        var metadata = Encoding.ASCII.GetString(record[..tab]);
        var pathBytes = record[(tab + 1)..];
        if (pathBytes.Length > maxPathBytes)
            throw new InvalidOperationException(
                $"a git tree entry path is {pathBytes.Length} UTF-8 bytes long, exceeding the " +
                $"configured maximum of {maxPathBytes}; failing closed.");

        var parts = metadata.Split(' ');
        if (parts.Length != 3)
            throw new InvalidOperationException(
                $"git ls-tree metadata is malformed: '{metadata}'.");
        var (mode, objectType, objectSha) = (parts[0], parts[1], parts[2]);

        if (!RepositoryPathPolicy.IsSupportedMode(mode))
            throw new InvalidOperationException(
                $"git tree entry uses unsupported mode '{mode}' (type '{objectType}'): v1 " +
                "materializes regular files (100644) and executables (100755) only — " +
                "symlinks (120000), gitlinks/submodules (160000), trees in invalid positions " +
                "and unknown modes fail closed.");
        if (objectType != "blob")
            throw new InvalidOperationException(
                $"git tree entry with mode '{mode}' has unexpected object type '{objectType}': " +
                "non-blob objects (trees in invalid positions, commit references, …) fail closed.");
        if (objectSha.Length != 40 || !IsHex(objectSha))
            throw new InvalidOperationException(
                $"git ls-tree metadata carries a malformed object id: '{objectSha}'.");

        string path;
        try
        {
            path = StrictUtf8.GetString(pathBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidOperationException(
                "git tree entry path is not valid UTF-8; failing closed.");
        }
        if (!RepositoryPathPolicy.IsSafeTreePath(path))
            throw new InvalidOperationException(
                $"git tree entry path {DescribePath(path)} is not a safe repository-relative " +
                "path: absolute, drive-relative, UNC, stream or colon forms, traversal ('..'), " +
                "empty segments, backslashes, control characters, non-NFC Unicode forms, " +
                "'.git'-segment names and non-canonical forms fail closed.");

        return new GitTreeEntry(mode, objectType, objectSha, path);
    }

    private static bool IsHex(string value) =>
        value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    /// <summary>Makes an unsafe path displayable without ever emitting raw control bytes.</summary>
    private static string DescribePath(string path) => string.Concat(path.Select(c =>
        char.IsControl(c) ? $"\\x{(int)c:x2}" : c.ToString()));
}
