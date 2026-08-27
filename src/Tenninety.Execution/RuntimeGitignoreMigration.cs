using Tenninety.Core;

namespace Tenninety.Execution;

/// <summary>Makes sure older workspaces also ignore transient runtime files.</summary>
public static class RuntimeGitignoreMigration
{
    public static readonly string[] RequiredLines =
    [
        "state.json",
        "state.json.tmp*",
        "state.json.lock",
        "audit-log.jsonl",
        "control/",
    ];

    public static string Contents => string.Join('\n', RequiredLines) + "\n";

    public static bool Ensure(string repoPath)
    {
        var file = Path.Combine(repoPath, TenNinety.StateDir, ".gitignore");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : new List<string>();
        var missing = RequiredLines
            .Where(l => !lines.Any(existing => existing.Trim() == l)).ToList();
        if (missing.Count == 0) return false;
        File.AppendAllLines(file, missing);
        return true;
    }
}
