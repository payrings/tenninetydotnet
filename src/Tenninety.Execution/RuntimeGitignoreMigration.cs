using Tenninety.Core;
using Tenninety.Git;

namespace Tenninety.Execution;

/// <summary>Makes sure older workspaces also ignore transient runtime files.</summary>
public static class RuntimeGitignoreMigration
{
    public static readonly string[] RequiredLines =
    [
        "state.json",
        "state.json.tmp*",
        "state.json.lock",
        "promotion-transaction.json",
        "promotion-transaction.json.tmp*",
        "promotion-transaction.json.lock",
        "audit-log.jsonl",
        "sandbox-resources.json",
        "sandbox-resources.json.tmp*",
        "sandbox-resources.json.lock",
        "control/",
    ];

    public static string Contents => string.Join('\n', RequiredLines) + "\n";

    public static bool PromotionArtifactsAreIgnored(IGitService git)
    {
        var promotionPath = $"{TenNinety.StateDir}/{TenNinety.PromotionFile}";
        return new[]
        {
            promotionPath,
            promotionPath + ".tmp-migration-probe",
            promotionPath + ".lock",
        }.All(git.IsPathIgnored);
    }

    public static int CountPromotionJournalsInOtherWorktrees(IGitService git)
    {
        var current = Path.GetFullPath(git.RepoPath);
        return git.WorktreePaths()
            .Where(path => !string.Equals(
                Path.GetFullPath(path), current, StringComparison.Ordinal))
            .Select(path => Path.Combine(
                path, TenNinety.StateDir, TenNinety.PromotionFile))
            .Count(File.Exists);
    }

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
