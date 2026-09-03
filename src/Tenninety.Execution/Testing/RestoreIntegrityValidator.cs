using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Execution.Testing;

internal sealed record RestoreIntegrityLimits(
    int MaxDerivedFiles,
    long MaxDerivedFileBytes,
    long MaxDerivedLogicalBytes,
    long MaxDerivedAllocatedBytes,
    int MaxDepth);

internal sealed record RestoreIntegrityResult(
    string DerivedOutputSha256,
    int DerivedFiles,
    long DerivedLogicalBytes,
    long DerivedAllocatedBytes);

/// <summary>No-follow Linux manifest validator for the optional Restore phase. Existing
/// candidate/control entries must retain identity, type, bytes and security metadata; only
/// bounded regular single-link derived output beneath fixed package/control/project obj roots
/// is accepted.</summary>
internal sealed class RestoreIntegrityValidator
{
    private const int S_IFMT = 0xF000;
    private const int S_IFDIR = 0x4000;
    private const int S_IFREG = 0x8000;
    private const int WriteByGroupOrOther = 0x12;
    private const int SpecialModeBits = 0xE00;

    internal sealed record Entry(
        string Path,
        bool IsDirectory,
        ulong Device,
        ulong Inode,
        ulong Links,
        uint Mode,
        uint Uid,
        uint Gid,
        long Size,
        long AllocatedBytes,
        long MtimeSeconds,
        ulong MtimeNanoseconds,
        long CtimeSeconds,
        ulong CtimeNanoseconds,
        string ContentSha256);

    internal sealed class Manifest
    {
        public required string Root { get; init; }
        public required IReadOnlyDictionary<string, Entry> Entries { get; init; }
        public required IReadOnlySet<string> ProjectDirectories { get; init; }
        public required uint RootUid { get; init; }
        public required uint RootGid { get; init; }
    }

    public Manifest CaptureBaseline(
        string workspaceRoot, long maxLogicalBytes, int maxFiles, int maxDepth,
        CancellationToken ct)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var entries = Capture(root, maxLogicalBytes, maxLogicalBytes, maxFiles, maxDepth, ct);
        var rootEntry = entries[""];
        var projectDirectories = entries.Values
            .Where(entry => !entry.IsDirectory &&
                entry.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(entry => Parent(entry.Path))
            .ToHashSet(StringComparer.Ordinal);
        return new Manifest
        {
            Root = root,
            Entries = entries,
            ProjectDirectories = projectDirectories,
            RootUid = rootEntry.Uid,
            RootGid = rootEntry.Gid,
        };
    }

    /// <summary>Captures trusted control entries created after the candidate baseline and
    /// before Restore. Any unexpected host-side addition fails closed.</summary>
    public IReadOnlyDictionary<string, Entry> CaptureTrustedControl(
        Manifest baseline, long maxLogicalBytes, int maxFiles, int maxDepth,
        CancellationToken ct)
    {
        var current = Capture(
            baseline.Root, maxLogicalBytes, maxLogicalBytes, maxFiles, maxDepth, ct);
        var control = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (path, entry) in current)
        {
            if (baseline.Entries.ContainsKey(path)) continue;
            if (path != ".tenninety" &&
                path != ".tenninety/restore-control" &&
                !path.StartsWith(".tenninety/restore-control/", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "an unexpected path appeared while preparing trusted Restore control data.");
            control.Add(path, entry);
        }
        return control;
    }

    public RestoreIntegrityResult VerifyPostRestore(
        Manifest baseline,
        IReadOnlyDictionary<string, Entry> trustedControl,
        RestoreIntegrityLimits limits,
        CancellationToken ct)
    {
        var baselineLogical = baseline.Entries.Values
            .Where(entry => !entry.IsDirectory).Sum(entry => entry.Size);
        var baselineAllocated = baseline.Entries.Values
            .Where(entry => !entry.IsDirectory).Sum(entry => entry.AllocatedBytes);
        var post = Capture(
            baseline.Root,
            checked(baselineLogical + limits.MaxDerivedLogicalBytes),
            checked(baselineAllocated + limits.MaxDerivedAllocatedBytes),
            checked(baseline.Entries.Count + trustedControl.Count + limits.MaxDerivedFiles + 8),
            limits.MaxDepth,
            ct);

        foreach (var (path, before) in baseline.Entries)
        {
            if (!post.TryGetValue(path, out var after))
                throw new InvalidOperationException(
                    "Restore removed an existing candidate entry; integrity validation failed.");
            RequirePreserved(before, after, allowDirectoryChildren: true);
        }
        foreach (var (path, before) in trustedControl)
        {
            if (!post.TryGetValue(path, out var after))
                throw new InvalidOperationException(
                    "Restore removed trusted control data; integrity validation failed.");
            // The trusted .tenninety parent necessarily gains the fixed restore-packages
            // sibling. The restore-control subtree itself must remain byte/metadata stable.
            RequirePreserved(before, after,
                allowDirectoryChildren: path == ".tenninety");
        }

        var derived = post
            .Where(pair => !baseline.Entries.ContainsKey(pair.Key) &&
                           !trustedControl.ContainsKey(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList();
        var fileCount = 0;
        long logicalBytes = 0;
        long allocatedBytes = 0;
        var canonical = new StringBuilder();
        foreach (var (path, entry) in derived)
        {
            if (!IsAllowedDerivedPath(path, baseline.ProjectDirectories))
                throw new InvalidOperationException(
                    "Restore created output outside the fixed package/project obj roots.");
            RequireSafeNewEntry(entry, baseline.RootUid, baseline.RootGid);
            if (!entry.IsDirectory)
            {
                fileCount++;
                logicalBytes = checked(logicalBytes + entry.Size);
                allocatedBytes = checked(allocatedBytes + entry.AllocatedBytes);
                if (entry.Size > limits.MaxDerivedFileBytes)
                    throw new InvalidOperationException(
                        "Restore created a file larger than the per-file derived-output bound.");
            }
            canonical.Append(path).Append('\0')
                .Append(entry.IsDirectory ? "d" : "f").Append('\0')
                .Append(entry.Mode).Append('\0')
                .Append(entry.Size).Append('\0')
                .Append(entry.AllocatedBytes).Append('\0')
                .Append(entry.ContentSha256).Append('\n');
        }
        if (fileCount > limits.MaxDerivedFiles ||
            logicalBytes > limits.MaxDerivedLogicalBytes ||
            allocatedBytes > limits.MaxDerivedAllocatedBytes)
            throw new InvalidOperationException(
                "Restore derived output exceeded the file, logical-size or allocated-size bound.");

        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()))).ToLowerInvariant();
        return new RestoreIntegrityResult(digest, fileCount, logicalBytes, allocatedBytes);
    }

    private static Dictionary<string, Entry> Capture(
        string root,
        long maxLogicalBytes,
        long maxAllocatedBytes,
        int maxFiles,
        int maxDepth,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException(
                "Restore integrity validation requires Linux no-follow metadata semantics.");
        var result = new Dictionary<string, Entry>(StringComparer.Ordinal);
        var pending = new Stack<(string FullPath, string RelativePath, int Depth)>();
        pending.Push((root, "", 0));
        long logicalBytes = 0;
        long allocatedBytes = 0;
        var entries = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (fullPath, relativePath, depth) = pending.Pop();
            entries++;
            if (entries > maxFiles)
                throw new InvalidOperationException(
                    "Restore workspace exceeded its bounded manifest entry capacity.");
            if (depth > maxDepth)
                throw new InvalidOperationException(
                    "Restore workspace depth exceeded the configured bound.");
            var stat = StatPath(fullPath);
            var kind = (int)(stat.StMode & S_IFMT);
            if (kind is not (S_IFDIR or S_IFREG))
                throw new InvalidOperationException(
                    "Restore workspace contains a link or special filesystem entry.");
            if ((stat.StMode & (WriteByGroupOrOther | SpecialModeBits)) != 0)
                throw new InvalidOperationException(
                    "Restore workspace contains widened or special permission bits.");

            var isDirectory = kind == S_IFDIR;
            var hash = "";
            if (!isDirectory)
            {
                if (stat.StNlink != 1)
                    throw new InvalidOperationException(
                        "Restore workspace contains a hardlinked regular file.");
                logicalBytes = checked(logicalBytes + stat.StSize);
                var allocated = checked(stat.StBlocks * 512L);
                allocatedBytes = checked(allocatedBytes + allocated);
                if (logicalBytes > maxLogicalBytes ||
                    allocatedBytes > maxAllocatedBytes)
                    throw new InvalidOperationException(
                        "Restore workspace exceeded its bounded manifest capacity.");
                hash = HashRegularFile(fullPath, stat.StSize);
            }
            result.Add(relativePath, new Entry(
                relativePath, isDirectory, stat.StDev, stat.StIno, stat.StNlink,
                stat.StMode, stat.StUid, stat.StGid, stat.StSize,
                checked(stat.StBlocks * 512L), stat.StMtime, stat.StMtimeNsec,
                stat.StCtime, stat.StCtimeNsec, hash));

            if (!isDirectory) continue;
            foreach (var child in Directory.EnumerateFileSystemEntries(fullPath)
                         .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(child);
                if (name.Length == 0 || name is "." or ".." || name.Contains('/') ||
                    name.Contains('\\') || name.Contains('\0'))
                    throw new InvalidOperationException(
                        "Restore workspace contains an unsafe path component.");
                var childRelative = relativePath.Length == 0
                    ? name
                    : relativePath + "/" + name;
                pending.Push((child, childRelative, depth + 1));
            }
        }
        return result;
    }

    private static string HashRegularFile(string path, long expectedSize)
    {
        using var opened = TrustedFileReader.OpenRegularFileNoFollow(path);
        if (opened.Length != expectedSize)
            throw new InvalidOperationException(
                "Restore workspace file size changed during manifest capture.");
        using var stream = new FileStream(
            opened.Handle, FileAccess.Read, bufferSize: 81920, isAsync: false);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long readTotal = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            readTotal += read;
            hasher.AppendData(buffer, 0, read);
        }
        opened.VerifyUnchanged(readTotal);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    private static void RequirePreserved(Entry before, Entry after, bool allowDirectoryChildren)
    {
        if (before.IsDirectory != after.IsDirectory || before.Device != after.Device ||
            before.Inode != after.Inode || before.Mode != after.Mode ||
            before.Uid != after.Uid || before.Gid != after.Gid)
            throw new InvalidOperationException(
                "Restore replaced an existing entry or changed its type/security metadata.");
        if (before.IsDirectory)
        {
            if (!allowDirectoryChildren &&
                (before.MtimeSeconds != after.MtimeSeconds ||
                 before.MtimeNanoseconds != after.MtimeNanoseconds))
                throw new InvalidOperationException(
                    "Restore changed a trusted control directory.");
            return;
        }
        if (before.Links != after.Links || before.Size != after.Size ||
            before.MtimeSeconds != after.MtimeSeconds ||
            before.MtimeNanoseconds != after.MtimeNanoseconds ||
            before.CtimeSeconds != after.CtimeSeconds ||
            before.CtimeNanoseconds != after.CtimeNanoseconds ||
            !string.Equals(before.ContentSha256, after.ContentSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Restore changed an existing candidate/control file.");
    }

    private static void RequireSafeNewEntry(Entry entry, uint rootUid, uint rootGid)
    {
        if (entry.Uid != rootUid || entry.Gid != rootGid ||
            (entry.Mode & (WriteByGroupOrOther | SpecialModeBits)) != 0 ||
            !entry.IsDirectory && entry.Links != 1)
            throw new InvalidOperationException(
                "Restore output has an unexpected owner, permissions or link count.");
    }

    private static bool IsAllowedDerivedPath(
        string path, IReadOnlySet<string> projectDirectories)
    {
        if (path is ".tenninety/restore-packages" ||
            path.StartsWith(".tenninety/restore-packages/", StringComparison.Ordinal))
            return true;
        foreach (var projectDirectory in projectDirectories)
        {
            var obj = projectDirectory.Length == 0 ? "obj" : projectDirectory + "/obj";
            if (path == obj || path.StartsWith(obj + "/", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static Stat StatPath(string path)
    {
        if (lstat(path, out var stat) != 0)
            throw new InvalidOperationException(
                "Restore workspace entry could not be inspected no-follow (error " +
                Marshal.GetLastWin32Error() + ").");
        return stat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Stat
    {
        public ulong StDev;
        public ulong StIno;
        public ulong StNlink;
        public uint StMode;
        public uint StUid;
        public uint StGid;
        public uint Pad0;
        public ulong StRdev;
        public long StSize;
        public long StBlksize;
        public long StBlocks;
        public long StAtime;
        public ulong StAtimeNsec;
        public long StMtime;
        public ulong StMtimeNsec;
        public long StCtime;
        public ulong StCtimeNsec;
        public long Unused0;
        public long Unused1;
        public long Unused2;
    }

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int lstat(string pathname, out Stat stat);
}
