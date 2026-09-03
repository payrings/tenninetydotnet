using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Narrow Linux host-side reader for workspace files, built on no-follow native semantics so
/// the scanner can never be tricked by symlinks and never blocks on or opens a FIFO, socket
/// or device:
///
///  1. the entry is INSPECTED with `open(O_PATH|O_NOFOLLOW|O_CLOEXEC)` — an O_PATH open does
///     not block on FIFOs and does not trigger device side effects. NOTE: with O_NOFOLLOW,
///     Linux OPENS a final-component symlink itself (the descriptor refers to the link, not
///     the target) instead of failing with ELOOP; ELOOP only occurs for symlink loops. The
///     security decision is therefore the descriptor `fstat`, which must show S_IFREG — an
///     S_IFLNK descriptor is rejected;
///  2. descriptor metadata (fstat) must prove the entry is a REGULAR file — everything else
///     fails closed;
///  3. the read handle is opened separately with `open(O_RDONLY|O_NOFOLLOW|O_CLOEXEC)` and its
///     descriptor metadata must refer to the SAME device/inode as the inspection descriptor;
///  4. mode (including the executable bit), size and timestamps come from the opened
///     descriptor, never from a pathname lookup;
///  5. after streaming, the caller re-verifies device, inode, exact mode/type, initial size,
///     final size and mtime/ctime (seconds and nanoseconds) via <see cref="VerifyUnchanged"/>
///     so unexpected concurrent modification fails closed. Reading does not alter ctime.
///
/// Hardlinked regular files open and read as ordinary content; no link relationship is
/// recorded or preserved anywhere. There is deliberately no fallback: on a platform without
/// this implementation (non-Linux) scanning fails closed. Handles are owned by the caller and
/// must be disposed.
/// </summary>
internal static class TrustedFileReader
{
    private const int O_RDONLY = 0x0000;
    // Linux header values (asm-generic/fcntl.h): O_CLOEXEC 02000000 octal = 0x80000;
    // O_NOFOLLOW 0400000 octal = hex 0x20000; O_NOATIME 04000000 octal = hex 0x40000;
    // O_PATH 020000000 octal = hex 0x200000.
    private const int O_CLOEXEC = 0x80000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_NOATIME = 0x40000;
    private const int O_PATH = 0x200000;

    private const int S_IFMT = 0xF000;   // 0170000 octal
    private const int S_IFREG = 0x8000;  // 0100000 octal
    private const int S_IFLNK = 0xA000;  // 0120000 octal
    private const int S_IXUSR = 0x40;    // 0100 octal

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
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(int fd, out Stat stat);

    /// <summary>Device+inode identity of an OPENED file handle (fstat on the descriptor).
    /// Used by the daemon lock to prove the path still refers to the held file.</summary>
    internal static (ulong DeviceId, ulong InodeId) GetHandleDeviceAndInode(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException(
                "handle device/inode inspection requires Linux; refusing to guess.");
        if (fstat((int)handle.DangerousGetHandle(), out var stat) != 0)
            throw new InvalidOperationException(
                "the opened handle could not be stat-ed; failing closed.");
        return (stat.StDev, stat.StIno);
    }

    /// <summary>
    /// A proven-regular, no-follow opened workspace file plus the descriptor metadata
    /// captured at open time. <see cref="VerifyUnchanged"/> compares every captured field
    /// (device, inode, exact mode/type, size, mtime seconds+nanoseconds, ctime
    /// seconds+nanoseconds) against a fresh descriptor stat after streaming.
    /// </summary>
    public sealed class OpenedRegularFile : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly long _initialLength;
        private readonly uint _initialMode;
        private readonly ulong _initialDevice;
        private readonly ulong _initialInode;
        private readonly long _initialMtimeSeconds;
        private readonly ulong _initialMtimeNanoseconds;
        private readonly long _initialCtimeSeconds;
        private readonly ulong _initialCtimeNanoseconds;

        internal OpenedRegularFile(
            SafeFileHandle handle,
            long length,
            bool executable,
            ulong deviceId,
            ulong inodeId,
            uint mode,
            long mtimeSeconds,
            ulong mtimeNanoseconds,
            long ctimeSeconds,
            ulong ctimeNanoseconds)
        {
            _handle = handle;
            _initialLength = length;
            Executable = executable;
            _initialDevice = deviceId;
            _initialInode = inodeId;
            _initialMode = mode;
            _initialMtimeSeconds = mtimeSeconds;
            _initialMtimeNanoseconds = mtimeNanoseconds;
            _initialCtimeSeconds = ctimeSeconds;
            _initialCtimeNanoseconds = ctimeNanoseconds;
        }

        public SafeFileHandle Handle => _handle;
        public long Length => _initialLength;
        public bool Executable { get; }
        public ulong DeviceId => _initialDevice;
        public ulong InodeId => _initialInode;
        public uint Mode => _initialMode;

        public void Dispose() => _handle.Dispose();

        /// <summary>Re-stat-es the opened descriptor and requires that device, inode, exact
        /// mode/type, initial size, final size and mtime/ctime (seconds and nanoseconds) are
        /// all unchanged, and that <paramref name="expectedBytesRead"/> equals both the
        /// initial and the final size. Any difference fails closed.</summary>
        public void VerifyUnchanged(long expectedBytesRead)
        {
            if (fstat((int)_handle.DangerousGetHandle(), out var stat) != 0)
                throw new InvalidOperationException(
                    "the opened workspace file could not be re-inspected after reading; " +
                    "extraction fails closed.");
            if ((stat.StMode & S_IFMT) != S_IFREG)
                throw new InvalidOperationException(
                    "the opened workspace file is no longer a regular file after reading; " +
                    "extraction fails closed.");
            if (stat.StDev != _initialDevice || stat.StIno != _initialInode)
                throw new InvalidOperationException(
                    "the opened workspace file was replaced by a different inode while it " +
                    "was being read; extraction fails closed.");
            if (stat.StMode != _initialMode)
                throw new InvalidOperationException(
                    "the opened workspace file changed mode while it was being read; " +
                    "extraction fails closed.");
            if (stat.StSize != _initialLength)
                throw new InvalidOperationException(
                    "the opened workspace file changed size while it was being read; " +
                    "extraction fails closed.");
            if (expectedBytesRead != _initialLength || stat.StSize != expectedBytesRead)
                throw new InvalidOperationException(
                    "the bytes streamed from the opened workspace file do not equal its " +
                    "initial and final size; extraction fails closed.");
            if (stat.StMtime != _initialMtimeSeconds ||
                stat.StMtimeNsec != _initialMtimeNanoseconds ||
                stat.StCtime != _initialCtimeSeconds ||
                stat.StCtimeNsec != _initialCtimeNanoseconds)
                throw new InvalidOperationException(
                    "the opened workspace file changed timestamps while it was being read " +
                    "(content or metadata changed); extraction fails closed.");
        }
    }

    /// <summary>Opens the entry with no-follow semantics and proves it is a regular file.</summary>
    public static OpenedRegularFile OpenRegularFileNoFollow(string path)
    {
        // 1. Inspection open: O_PATH never blocks on FIFOs and never triggers device side
        //    effects. With O_NOFOLLOW a final-component symlink OPENS AS ITSELF (no ELOOP
        //    for a single link) — the fstat below must therefore reject its S_IFLNK type.
        //    ELOOP still occurs for symlink loops.
        var inspectFd = open(path, O_PATH | O_NOFOLLOW | O_CLOEXEC);
        if (inspectFd < 0)
            throw new InvalidOperationException(
                Marshal.GetLastWin32Error() switch
                {
                    2 /* ENOENT */ => "the workspace entry disappeared during the scan; extraction fails closed.",
                    40 /* ELOOP */ => "the workspace entry is part of a symlink loop; extraction fails closed.",
                    20 /* ENOTDIR */ => "the workspace path is not a directory chain; extraction fails closed.",
                    _ => $"the workspace entry could not be inspected safely (error {Marshal.GetLastWin32Error()}); extraction fails closed.",
                });
        try
        {
            if (fstat(inspectFd, out var inspected) != 0)
                throw new InvalidOperationException(
                    "the workspace entry could not be stat-ed safely; extraction fails closed.");
            if ((inspected.StMode & S_IFMT) != S_IFREG)
                throw new InvalidOperationException(
                    (inspected.StMode & S_IFMT) == S_IFLNK
                        ? "the workspace entry is a symlink (including links to existing " +
                          "regular files); extraction fails closed."
                        : "the workspace entry is not a regular file (FIFO, socket, device " +
                          "or directory); extraction fails closed.");

            // 2. Read open with no-follow and no-atime: on strictatime/relatime mounts a
            //    plain read can update atime (which in turn bumps ctime), making the later
            //    VerifyUnchanged timestamp comparison fail spuriously. O_NOATIME keeps the
            //    metadata untouched; when it is refused (EPERM: not the owner, or EINVAL:
            //    unsupported), fall back to the plain no-follow open and let the strict
            //    verification fail closed as before.
            //    then prove by descriptor identity that it refers
            //    to the same inode as the inspection open (no pathname swap in between).
            var readFd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NOATIME);
            if (readFd < 0)
            {
                var noAtimeError = Marshal.GetLastWin32Error();
                if (noAtimeError is not (1 /* EPERM */ or 22 /* EINVAL */))
                    throw new InvalidOperationException(
                        $"the workspace file could not be opened no-follow (error {noAtimeError}); extraction fails closed.");
                readFd = open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
            }
            if (readFd < 0)
                throw new InvalidOperationException(
                    $"the workspace file could not be opened no-follow (error {Marshal.GetLastWin32Error()}); extraction fails closed.");
            if (fstat(readFd, out var opened) != 0)
            {
                _ = close(readFd);
                throw new InvalidOperationException(
                    "the opened workspace file could not be stat-ed; extraction fails closed.");
            }
            if ((opened.StMode & S_IFMT) != S_IFREG ||
                opened.StDev != inspected.StDev ||
                opened.StIno != inspected.StIno ||
                opened.StMode != inspected.StMode)
            {
                _ = close(readFd);
                throw new InvalidOperationException(
                    "the opened workspace file does not refer to the inspected regular " +
                    "inode; extraction fails closed.");
            }

            var handle = new SafeFileHandle(readFd, ownsHandle: true);
            return new OpenedRegularFile(
                handle,
                opened.StSize,
                (opened.StMode & S_IXUSR) != 0,
                opened.StDev,
                opened.StIno,
                opened.StMode,
                opened.StMtime,
                opened.StMtimeNsec,
                opened.StCtime,
                opened.StCtimeNsec);
        }
        finally
        {
            _ = close(inspectFd);
        }
    }
}
