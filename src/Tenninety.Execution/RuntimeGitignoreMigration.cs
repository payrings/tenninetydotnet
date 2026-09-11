using Tenninety.Core;
using Tenninety.Git;

namespace Tenninety.Execution;

/// <summary>Makes sure older workspaces also ignore transient runtime files.</summary>
public static class RuntimeGitignoreMigration
{
    public static readonly string[] RequiredLines =
    [
        "plan.json.tmp*",
        "state.json",
        "state.json.tmp*",
        "state.json.lock",
        "promotion-transaction.json",
        "promotion-transaction.json.tmp*",
        "promotion-transaction.json.lock",
        "pivot-transaction.json",
        "pivot-transaction.json.tmp*",
        "pivot-transaction.json.lock",
        "pivot-plan.next.json",
        "pivot-plan.next.json.tmp*",
        "pivot-state.next.json",
        "pivot-state.next.json.tmp*",
        "pivot-state.next.json.lock",
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

    public static bool PivotArtifactsAreIgnored(IGitService git)
    {
        var root = $"{TenNinety.StateDir}/";
        return new[]
        {
            root + TenNinety.PivotFile,
            root + TenNinety.PivotFile + ".tmp-migration-probe",
            root + TenNinety.PivotFile + ".lock",
            root + TenNinety.PivotPlanFile,
            root + TenNinety.PivotPlanFile + ".tmp-migration-probe",
            root + TenNinety.PivotStateFile,
            root + TenNinety.PivotStateFile + ".tmp-migration-probe",
            root + TenNinety.PivotStateFile + ".lock",
        }.All(git.IsPathIgnored);
    }

    public static bool RecoveryArtifactsAreIgnored(IGitService git) =>
        PromotionArtifactsAreIgnored(git) && PivotArtifactsAreIgnored(git) &&
        git.IsPathIgnored($"{TenNinety.StateDir}/{TenNinety.PlanFile}.tmp-migration-probe");

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

    public static int CountPivotJournalsInOtherWorktrees(IGitService git)
    {
        var current = Path.GetFullPath(git.RepoPath);
        return git.WorktreePaths()
            .Where(path => !string.Equals(
                Path.GetFullPath(path), current, StringComparison.Ordinal))
            .Select(path => Path.Combine(path, TenNinety.StateDir, TenNinety.PivotFile))
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
