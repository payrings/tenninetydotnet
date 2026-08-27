using System.Text;
using Tenninety.Core;

namespace Tenninety.Execution;

/// <summary>
/// Cross-process guard so two daemons can never mutate the same workspace concurrently.
/// The persistent lock file carries the owning PID for diagnostics; the exclusive OS file
/// handle is the lock, so process exit releases it without stale-file recovery races.
/// </summary>
public static class DaemonLock
{
    public static IDisposable Acquire(string root)
    {
        // Keep the lock outside the worktree. Linked worktrees store `.git` as a pointer file,
        // so resolve their shared common Git directory instead of assuming `.git/` is a folder.
        var dir = Path.Combine(ResolveCommonGitDirectory(root), "tenninety");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "daemon.lock");

        try
        {
            // The OS lock, not file existence, owns exclusion. A process crash releases the
            // handle automatically, so a stale file never needs unsafe check/delete recovery.
            var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            held.SetLength(0);
            using var writer = new StreamWriter(held, Encoding.UTF8, bufferSize: 128, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            held.Flush(flushToDisk: true);
            return held;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                "another tenninety daemon appears to be running for this workspace.", ex);
        }
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
