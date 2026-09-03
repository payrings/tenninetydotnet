using System.Runtime.InteropServices;
using System.Text;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Shared destructive-deletion policy for Tenninety-managed attempt directories (Tester
/// attempt workspaces and preflight probe workspaces). Deleting through this helper is the
/// ONLY permitted way to remove a managed child directory:
///
///  1. both paths pass strict absolute-shape validation;
///  2. the managed-root chain is REVALIDATED immediately before the deletion: every component
///     from the filesystem root down to and including the managed root must exist as a real
///     directory and none may be a symlink/reparse point — a root or ancestor that was
///     replaced by a redirect refuses the deletion instead of following it;
///  3. the target must be a STRICT direct child of the managed root — the root itself, deeper
///     descendants and unrelated paths are refused;
///  4. no component between the managed root and the target may be a symlink; deletion can
///     never follow a workspace-created link out of the tree;
///  5. presence and TYPE of the target are checked NO-FOLLOW (Linux lstat; conservative
///     managed fallback elsewhere): genuine absence is distinguished from an existing regular
///     file, special file (FIFO/socket/device) or redirect — a non-directory entry is NOT
///     absence. An inspection failure never counts as absence, and an unexpected entry type
///     is PRESERVED and reported (infrastructure/retention failure) instead of being deleted;
///  6. after the recursive deletion the target's absence is positively verified again
///     no-follow.
///
/// Threat-model honesty: these checks close the redirect and containment gaps for the
/// trusted host's own cleanup decisions, but they cannot make arbitrary same-user concurrent
/// filesystem mutation impossible; an attacker running as the same user retains the usual
/// POSIX abilities (documented limitation, not a claimed guarantee).
/// </summary>
internal static class TrustedWorkspaceDeletion
{
    /// <summary>No-follow classification of one filesystem entry. Absence is proven only by a
    /// positive ENOENT; every other outcome is an existing entry or an inspection failure.</summary>
    internal enum ManagedEntryKind
    {
        /// <summary>Positively proven absent (lstat ENOENT / no entry).</summary>
        Absent,
        /// <summary>The entry is a real directory (no final-component symlink).</summary>
        RealDirectory,
        /// <summary>The entry is a regular file (no final-component symlink).</summary>
        RealFile,
        /// <summary>The entry exists but is unexpected: a FIFO, socket, device or redirect.
        /// It is never deleted; callers retain and report.</summary>
        UnexpectedEntry,
    }

    /// <summary>Deletes one managed attempt/probe child directory. When
    /// <paramref name="deleteOverride"/> is supplied (test seam) it fully replaces the real
    /// deletion and receives the raw path.</summary>
    public static async Task DeleteAsync(
        string childPath, string managedRoot, Func<string, Task>? deleteOverride)
    {
        if (deleteOverride is { } delete)
        {
            await delete(childPath);
            return;
        }
        DeleteManagedChildDirectory(childPath, managedRoot);
    }

    /// <summary>Validated, revalidated and absence-verified deletion of one direct child of
    /// the managed root. Throws (fail closed) instead of deleting anything unsafe.</summary>
    public static void DeleteManagedChildDirectory(string childPath, string managedRoot)
    {
        var root = TrustedPathValidation.ValidateAbsoluteShape(managedRoot, "managed workspace root");
        var child = TrustedPathValidation.ValidateAbsoluteShape(childPath, "managed attempt workspace");

        // Immediate pre-deletion revalidation of the whole root chain: the root itself and
        // every ancestor must still be real directories (no redirects, no replacement).
        TrustedPathValidation.EnsureRealDirectoryChain(root, "managed workspace root");

        if (child == root || !child.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "refusing to delete: the target directory is not a strict physical child of " +
                "the managed root.");
        if (child.Split('/').Length - root.Split('/').Length != 1)
            throw new InvalidOperationException(
                "refusing to delete: only a direct attempt child of the managed root may be " +
                "removed (never a deeper or unrelated path).");

        // Never follow a link on any INTERMEDIATE component between the managed root and
        // the attempt directory. (The attempt directory itself — a strict direct child — is
        // classified no-follow below; note the historical per-segment check here also ran
        // over the final component, where a missing entry would have been misread as a
        // redirect, making the already-absent case unreachable.)
        var segments = child[(root.Length + 1)..].Split('/');
        var current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            current = $"{current}/{segments[i]}";
            if (TrustedPathValidation.IsReparsePoint(current))
                throw new InvalidOperationException(
                    "refusing to delete: the attempt path contains a symlink component; " +
                    "deleting through a redirect is never permitted.");
        }

        // No-follow presence/type check: genuine absence, a real directory and an unexpected
        // entry (regular file, special file, redirect) are distinguished. An existing
        // non-directory entry is NOT "already absent" — it is unexpected and is preserved.
        var kind = InspectEntryNoFollow(child);
        switch (kind)
        {
            case ManagedEntryKind.Absent:
                // Already absent: the deletion goal is already proven, nothing to do.
                return;
            case ManagedEntryKind.RealDirectory:
                break; // the only deletable shape
            default:
                throw new InvalidOperationException(
                    "refusing to delete: the attempt path exists but is not a real directory " +
                    "(a regular file, special file or redirect is unexpected); the entry is " +
                    "preserved and reported instead of deleted.");
        }

        Directory.Delete(child, recursive: true);
        if (InspectEntryNoFollow(child) != ManagedEntryKind.Absent)
            throw new InvalidOperationException(
                "the attempt workspace is still present after the deletion attempt.");
    }

    /// <summary>Removes an automatically owned managed root: the chain is revalidated, the
    /// entry must be provably present as a REAL directory, and it must be provably EMPTY
    /// before a NON-RECURSIVE deletion. Unexpected contents (or any revalidation/inspection
    /// failure) throw so the caller can retain and report instead of deleting something it
    /// does not own. Absence is positively re-verified after the deletion.</summary>
    public static void DeleteEmptyOwnedDirectory(string ownedRoot)
    {
        TrustedPathValidation.ValidateAbsoluteShape(ownedRoot, "owned managed root");

        // No-follow presence/type check FIRST: genuine absence positively ends the goal (a
        // no-op — and note the historical chain check ran before any absence check, so a
        // deleted root could never reach it). An unexpected entry type (a regular file,
        // special file or redirect at the root path) is refused BEFORE any further walking.
        var kind = InspectEntryNoFollow(ownedRoot);
        if (kind == ManagedEntryKind.Absent)
            return;
        if (kind != ManagedEntryKind.RealDirectory)
            throw new InvalidOperationException(
                "refusing to delete the owned managed root: the entry exists but is not a " +
                "real directory; the unexpected entry is preserved and reported.");

        // The chain is revalidated before the destructive step (the root itself and every
        // ancestor must be real directories; no redirects).
        TrustedPathValidation.EnsureRealDirectoryChain(ownedRoot, "owned managed root");

        if (Directory.EnumerateFileSystemEntries(ownedRoot).Any())
            throw new InvalidOperationException(
                "refusing to delete the owned managed root: it is not empty; the unexpected " +
                "contents are retained and reported instead of deleted.");
        Directory.Delete(ownedRoot, recursive: false);
        if (InspectEntryNoFollow(ownedRoot) != ManagedEntryKind.Absent)
            throw new InvalidOperationException(
                "the owned managed root is still present after the deletion attempt.");
    }

    // ---- no-follow entry inspection ------------------------------------------------------

    /// <summary>
    /// No-follow presence/type inspection. On Linux this is lstat — the entry itself is
    /// classified without following any final-component link: ENOENT proves absence, S_IFDIR
    /// (without S_IFLNK) proves a real directory, and every other type (regular file, FIFO,
    /// socket, device, symlink) is unexpected. Any OTHER inspection failure fails closed
    /// (it never counts as absence). Non-Linux hosts use a conservative managed fallback
    /// that cannot identify every special entry but never treats a provably existing entry
    /// as absent (its remaining blind spot is documented).
    /// </summary>
    internal static ManagedEntryKind InspectEntryNoFollow(string path)
    {
        if (OperatingSystem.IsLinux())
            return InspectEntryLstat(path);
        return InspectEntryManagedFallback(path);
    }

    private static ManagedEntryKind InspectEntryLstat(string path)
    {
        if (lstat(path, out var stat) != 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 2 /* ENOENT: positively proven absent */)
                return ManagedEntryKind.Absent;
            // Every other error is an INSPECTION FAILURE, never absence: fail closed so a
            // broken inspection can never be turned into a successful cleanup claim.
            throw new InvalidOperationException(
                "the managed workspace entry could not be inspected no-follow (error " +
                error.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "); the entry is preserved and the cleanup fails closed.");
        }
        return (stat.StMode & S_IFMT) switch
        {
            S_IFDIR => ManagedEntryKind.RealDirectory,
            S_IFREG => ManagedEntryKind.RealFile,
            _ => ManagedEntryKind.UnexpectedEntry, // symlink, FIFO, socket or device
        };
    }

    private static ManagedEntryKind InspectEntryManagedFallback(string path)
    {
        // Conservative managed fallback for non-Linux hosts. It distinguishes redirects and
        // regular files from real directories and genuine absence; exotic special entries
        // whose existence managed APIs cannot observe are treated as absent (the tester
        // deletion path itself is a Linux host path in this phase — documented limitation).
        var dir = new DirectoryInfo(path);
        if (dir.LinkTarget is not null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return ManagedEntryKind.UnexpectedEntry;
        if (dir.Exists) return ManagedEntryKind.RealDirectory;
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return ManagedEntryKind.UnexpectedEntry;
        if (file.Exists) return ManagedEntryKind.RealFile;
        return ManagedEntryKind.Absent;
    }

    // ---- Linux lstat interop (same layout and provenance as TrustedFileReader) -----------

    private const int S_IFMT = 0xF000;   // 0170000 octal
    private const int S_IFDIR = 0x4000;  // 0040000 octal
    private const int S_IFREG = 0x8000;  // 0100000 octal

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

    /// <summary>No-follow device+inode identity of a path, or null when the entry is
    /// positively absent. Used by the daemon lock to detect unlink/replacement of a held
    /// lock file. Inspection failures fail closed.</summary>
    internal static (ulong DeviceId, ulong InodeId)? InspectDeviceAndInodeNoFollow(string path)
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException(
                "no-follow device/inode inspection requires Linux; refusing to guess.");
        if (lstat(path, out var stat) != 0)
        {
            if (Marshal.GetLastWin32Error() == 2 /* ENOENT */) return null;
            throw new InvalidOperationException(
                "the path could not be inspected no-follow (error " +
                Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture) +
                "); failing closed.");
        }
        return (stat.StDev, stat.StIno);
    }
}
