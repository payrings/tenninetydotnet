using Tenninety.Core;

namespace Tenninety.Execution;

/// <summary>
/// Cross-process control channel between human commands (tenninety pause/stop, TUI keys)
/// and a running daemon.
///
/// Requests are LATCHED MARKER FILES under `.tenninety/control/`:
///   pause.request / stop.request
/// The daemon consumes them (reads-then-deletes) at every safe point: before each work
/// package, and after every coder/reviewer/tester stage. Because consumption happens on
/// disk, an external `tenninety pause` works even while the daemon is mid-attempt –
/// something the previous in-memory-only flags could never do.
/// </summary>
public static class ExecutionControl
{
    private static string ControlDir(string repoPath) =>
        Path.Combine(repoPath, TenNinety.StateDir, "control");

    public static void SetPause(string repoPath)
    {
        Directory.CreateDirectory(ControlDir(repoPath));
        File.WriteAllText(Path.Combine(ControlDir(repoPath), "pause.request"), "pause");
    }

    public static void SetStop(string repoPath)
    {
        Directory.CreateDirectory(ControlDir(repoPath));
        File.WriteAllText(Path.Combine(ControlDir(repoPath), "stop.request"), "stop");
    }

    public static void ClearAll(string repoPath)
    {
        foreach (var name in new[] { "pause.request", "stop.request" })
        {
            var path = Path.Combine(ControlDir(repoPath), name);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>Reads pending requests WITHOUT consuming them.</summary>
    public static (bool PauseRequested, bool StopRequested) ReadFlags(string repoPath)
    {
        var dir = ControlDir(repoPath);
        return (
            File.Exists(Path.Combine(dir, "pause.request")),
            File.Exists(Path.Combine(dir, "stop.request")));
    }

    /// <summary>Reads and consumes pending requests (one-shot semantics for the daemon).</summary>
    public static (bool PauseRequested, bool StopRequested) ConsumeFlags(string repoPath)
    {
        var dir = ControlDir(repoPath);
        return (
            Consume(Path.Combine(dir, "pause.request")),
            Consume(Path.Combine(dir, "stop.request")));
    }

    private static bool Consume(string path)
    {
        var claimed = path + $".consumed.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            File.Move(path, claimed);
            File.Delete(claimed);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // Another process consumed this marker; a newly-created marker remains for the next poll.
            return false;
        }
    }
}
