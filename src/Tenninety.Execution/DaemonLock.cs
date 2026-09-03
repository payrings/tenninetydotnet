using System.Text;
using Tenninety.Core;

namespace Tenninety.Execution;

/// <summary>
/// A live, typed daemon-lock lease. The lease knows which repository it guards, tracks
/// disposal REQUESTS separately from actual handle release, and supports scoped operation
/// guards: a trusted operation calls <see cref="BeginUseFor"/> once and holds the returned
/// guard for the whole operation. Disposal while a guard is active is DEFERRED — the
/// underlying OS file lock stays held until the last guard exits — so an outer lease cannot
/// release the OS lock while a promotion is still running. All state transitions are
/// synchronized; new operation guards are rejected once disposal has been requested.
/// Implements <see cref="IDisposable"/> so existing `using var` callers keep working.
/// </summary>
public sealed class DaemonLockLease : IDisposable
{
    private readonly FileStream _handle;
    private readonly object _stateLock = new();
    private readonly string _lockPath;
    private readonly ulong _lockDeviceId;
    private readonly ulong _lockInodeId;

    private bool _disposalRequested;
    private bool _handleReleased;
    private int _activeUses;

    internal DaemonLockLease(
        FileStream handle, string workspaceRoot, string canonicalGitIdentity,
        string lockPath, ulong lockDeviceId, ulong lockInodeId)
    {
        _handle = handle;
        WorkspaceRoot = workspaceRoot;
        CanonicalGitIdentity = canonicalGitIdentity;
        _lockPath = lockPath;
        _lockDeviceId = lockDeviceId;
        _lockInodeId = lockInodeId;
    }

    /// <summary>The full path of the workspace root this lease guards.</summary>
    public string WorkspaceRoot { get; }

    /// <summary>The canonical common-Git directory identity of the guarded workspace.</summary>
    public string CanonicalGitIdentity { get; }

    /// <summary>True once disposal has been requested (new operation guards are rejected
    /// from that moment, even if the OS handle is still held by an active guard).</summary>
    public bool IsDisposed => Volatile.Read(ref _disposalRequested);

    /// <summary>
    /// Atomically verifies the lease is live, verifies the normalized workspace root AND the
    /// resolved canonical common-Git-directory identity of
    /// <paramref name="repositoryPath"/>, increments the active-use count, and returns a
    /// scoped guard. The guard keeps the OS lock held until disposed, exactly once.
    /// </summary>
    internal DaemonLockOperationGuard BeginUseFor(string repositoryPath)
    {
        var normalized = Path.GetFullPath(repositoryPath);
        var canonicalGit = Path.GetFullPath(DaemonLock.ResolveCommonGitDirectory(normalized));
        lock (_stateLock)
        {
            if (_disposalRequested)
                throw new InvalidOperationException(
                    "the daemon lock lease has been disposed; the operation requires a live lock.");
            if (!string.Equals(WorkspaceRoot, normalized, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the daemon lock lease belongs to a different repository; refusing to operate.");
            if (!string.Equals(CanonicalGitIdentity, canonicalGit, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the daemon lock lease canonical Git identity does not match the " +
                    "requested repository; refusing to operate.");
            VerifyLockPathBinding();
            _activeUses++;
            return new DaemonLockOperationGuard(this);
        }
    }

    /// <summary>Fails closed unless the lease is live (disposal not requested) and belongs
    /// to the given repository: both the normalized workspace root AND the resolved
    /// canonical common-Git-directory identity must match. NOTE: an ACTIVE operation guard
    /// makes the lease live for its holder even after disposal was requested — disposal is
    /// deferred until the guard exits. Guard holders verify liveness AND repository binding
    /// via <see cref="DaemonLockOperationGuard.EnsureLiveFor"/>.</summary>
    internal void ThrowIfNotLiveFor(string repositoryPath)
    {
        var normalized = Path.GetFullPath(repositoryPath);
        var canonicalGit = Path.GetFullPath(DaemonLock.ResolveCommonGitDirectory(normalized));
        lock (_stateLock)
        {
            if (_disposalRequested)
                throw new InvalidOperationException(
                    "the daemon lock lease has been disposed; the operation requires a live lock.");
            if (!string.Equals(WorkspaceRoot, normalized, StringComparison.Ordinal) ||
                !string.Equals(CanonicalGitIdentity, canonicalGit, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the daemon lock lease belongs to a different repository; refusing to operate.");
            VerifyLockPathBinding();
        }
    }

    /// <summary>Unlink-while-held defense: the path must still refer to the exact file the
    /// lease holds. A deleted or replaced lock file means another process could hold a
    /// different "lock" at the same path; every operation refuses until the path is restored
    /// or the lease is disposed. Inspection failures fail closed.</summary>
    private void VerifyLockPathBinding()
    {
        if (!OperatingSystem.IsLinux()) return; // documented best-effort on non-Linux
        var pathIdentity = Sandbox.TrustedWorkspaceDeletion.InspectDeviceAndInodeNoFollow(_lockPath);
        if (pathIdentity is not { } identity ||
            identity.DeviceId != _lockDeviceId ||
            identity.InodeId != _lockInodeId)
            throw new InvalidOperationException(
                "the daemon lock file was unlinked or replaced while held; refusing to operate.");
    }

    /// <summary>Requests disposal. If an operation guard is active, the OS file lock remains
    /// held until the last guard exits; otherwise the handle is released immediately.
    /// Idempotent.</summary>
    public void Dispose()
    {
        FileStream? toRelease = null;
        lock (_stateLock)
        {
            if (_disposalRequested) return;
            _disposalRequested = true;
            if (_activeUses == 0)
            {
                _handleReleased = true;
                toRelease = _handle;
            }
        }
        toRelease?.Dispose();
    }

    private void OperationGuardExited()
    {
        FileStream? toRelease = null;
        lock (_stateLock)
        {
            _activeUses--;
            if (_disposalRequested && !_handleReleased && _activeUses == 0)
            {
                _handleReleased = true;
                toRelease = _handle;
            }
        }
        toRelease?.Dispose();
    }

    /// <summary>Scoped operation guard for a live lease. While this guard exists the OS
    /// lock is held: disposal of the outer lease is DEFERRED (marked requested, handle kept)
    /// until the guard exits. The guard holder verifies liveness AND repository binding via
    /// <see cref="EnsureLiveFor"/>; new guards are rejected once disposal was requested.</summary>
    internal sealed class DaemonLockOperationGuard : IDisposable
    {
        private readonly DaemonLockLease _owner;
        /// <summary>Exactly-once disposal gate: 1 once <see cref="Dispose"/> has won the
        /// atomic exchange; every other disposal call — concurrent or repeated — is a no-op,
        /// so the owner's active-use count can never be decremented twice.</summary>
        private int _disposed;

        internal DaemonLockOperationGuard(DaemonLockLease owner) => _owner = owner;

        /// <summary>The lease this guard was issued from; trusted operations use it to
        /// verify lease/repository binding without a check-then-use gap.</summary>
        internal DaemonLockLease Lease => _owner;

        /// <summary>Fails closed unless the OS lock is still held for this guard AND this
        /// guard was issued for exactly the given repository: the normalized workspace root
        /// AND the resolved canonical common-Git-directory identity of
        /// <paramref name="repositoryPath"/> must both match the owning lease (ordinal).
        /// While the guard is active the handle cannot have been released (disposal is
        /// deferred), so liveness can only fail if the guard itself was disposed; disposal
        /// of the OUTER lease after this guard was acquired does NOT invalidate it — the
        /// guard intentionally keeps the underlying OS handle alive until it exits.</summary>
        internal void EnsureLiveFor(string repositoryPath)
        {
            var normalized = Path.GetFullPath(repositoryPath);
            var canonicalGit = Path.GetFullPath(
                DaemonLock.ResolveCommonGitDirectory(normalized));
            lock (_owner._stateLock)
            {
                if (Volatile.Read(ref _disposed) != 0 || _owner._handleReleased)
                    throw new InvalidOperationException(
                        "the daemon lock operation guard is no longer live; the operation " +
                        "cannot continue without the lock.");
                if (!string.Equals(_owner.WorkspaceRoot, normalized, StringComparison.Ordinal) ||
                    !string.Equals(_owner.CanonicalGitIdentity, canonicalGit, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "the daemon lock operation guard belongs to a different repository; " +
                        "refusing to operate.");
            }
        }

        public void Dispose()
        {
            // Exactly-once gate: exactly one caller (concurrent or repeated) wins the
            // exchange and reports the guard's exit; all losers return without effect, so
            // the active-use count is decremented at most once per guard and never negative.
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.OperationGuardExited();
        }
    }
}

/// <summary>
/// Cross-process guard so two daemons can never mutate the same workspace concurrently.
/// The persistent lock file carries the owning PID for diagnostics; the exclusive OS file
/// handle is the lock, so process exit releases it without stale-file recovery races.
/// </summary>
public static class DaemonLock
{
    public static DaemonLockLease Acquire(string root)
    {
        var workspaceRoot = Path.GetFullPath(root);
        // Keep the lock outside the worktree. Linked worktrees store `.git` as a pointer file,
        // so resolve their shared common Git directory instead of assuming `.git/` is a folder.
        var gitIdentity = Path.GetFullPath(ResolveCommonGitDirectory(workspaceRoot));
        var dir = Path.Combine(gitIdentity, "tenninety");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "daemon.lock");

        try
        {
            // The lock path must be a real regular file, never a symlink/reparse point: a
            // hostile redirect could otherwise make SetLength truncate an arbitrary writable
            // file owned by this user.
            RejectRedirectedLockPath(path);

            // The OS lock, not file existence, owns exclusion. A process crash releases the
            // handle automatically, so a stale file never needs unsafe check/delete recovery.
            var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            // Verify the path still refers to the exact file we hold (unlink-while-held
            // detection): the path's no-follow device+inode identity must equal the held
            // descriptor's identity. A replaced or unlinked path refuses the lock.
            var lockPathIdentity = RejectReplacedLockPath(path, held);

            held.SetLength(0);
            using var writer = new StreamWriter(held, Encoding.UTF8, bufferSize: 128, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            held.Flush(flushToDisk: true);
            return new DaemonLockLease(
                held, workspaceRoot, gitIdentity, path,
                lockPathIdentity.DeviceId, lockPathIdentity.InodeId);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "another tenninety daemon appears to be running for this workspace.", ex);
        }
    }

    private static void RejectRedirectedLockPath(string path)
    {
        var kind = Sandbox.TrustedWorkspaceDeletion.InspectEntryNoFollow(path);
        if (kind is not (Sandbox.TrustedWorkspaceDeletion.ManagedEntryKind.Absent or
            Sandbox.TrustedWorkspaceDeletion.ManagedEntryKind.RealFile))
            throw new InvalidOperationException(
                "the daemon lock path is a symlink, reparse point or unexpected entry; " +
                "refusing to open it.");
    }

    private static (ulong DeviceId, ulong InodeId) RejectReplacedLockPath(
        string path, FileStream held)
    {
        if (!OperatingSystem.IsLinux())
            return (0, 0); // non-Linux: best-effort (documented limitation)
        var heldIdentity = Sandbox.TrustedFileReader.GetHandleDeviceAndInode(held.SafeFileHandle);
        var pathIdentity = Sandbox.TrustedWorkspaceDeletion.InspectDeviceAndInodeNoFollow(path);
        if (pathIdentity is not { } identity ||
            identity.DeviceId != heldIdentity.DeviceId ||
            identity.InodeId != heldIdentity.InodeId)
        {
            held.Dispose();
            throw new InvalidOperationException(
                "the daemon lock file was unlinked or replaced while acquiring; refusing a " +
                "split lock.");
        }
        return heldIdentity;
    }

    internal static string ResolveCommonGitDirectory(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit))
            throw new InvalidOperationException("workspace is not a git repository.");

        var pointer = File.ReadAllText(dotGit).Trim();
        const string prefix = "gitdir:";
        if (!pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("workspace has an invalid .git pointer file.");
        var gitDirValue = pointer[prefix.Length..].Trim();
        var gitDir = Path.GetFullPath(Path.IsPathRooted(gitDirValue)
            ? gitDirValue
            : Path.Combine(root, gitDirValue));

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile)) return gitDir;
        var commonDirValue = File.ReadAllText(commonDirFile).Trim();
        return Path.GetFullPath(Path.IsPathRooted(commonDirValue)
            ? commonDirValue
            : Path.Combine(gitDir, commonDirValue));
    }
}
