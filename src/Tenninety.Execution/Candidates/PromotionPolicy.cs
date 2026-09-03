using System.Text;
using Tenninety.Core.Models;
using Tenninety.Core.Security;

namespace Tenninety.Execution.Candidates;

/// <summary>Typed ordinary candidate rejection produced by the trusted promotion policy.</summary>
public sealed class CandidatePolicyRejectedException : Exception
{
    public CandidatePolicyRejectedException(string message) : base(message) { }
}

/// <summary>Options for the candidate promotion policy; all fail closed.</summary>
public sealed record PromotionPolicyOptions
{
    /// <summary>Maximum number of changed files in one candidate patch.</summary>
    public int MaxChangedFiles { get; init; } = 2000;

    /// <summary>Exact normalized CASE-SENSITIVE repository-relative paths of normally
    /// sensitive files a human explicitly allows the candidate to touch. Globs, absolute,
    /// traversal or case-mismatched spellings never authorize anything.</summary>
    public IReadOnlyList<string> AllowSensitivePaths { get; init; } = Array.Empty<string>();

    public void Validate()
    {
        if (MaxChangedFiles is < 1 or > 1_000_000)
            throw new InvalidOperationException(
                $"promotion MaxChangedFiles must be within [1, 1000000] but is {MaxChangedFiles}.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in AllowSensitivePaths)
        {
            if (!SandboxPromotionConfig.IsExactRelativePath(path))
                throw new InvalidOperationException(
                    "promotion allow_sensitive_paths entries must be exact normalized " +
                    $"repository-relative paths, but found '{path}'.");
            if (!seen.Add(path))
                throw new InvalidOperationException(
                    $"promotion allow_sensitive_paths contains the duplicate entry '{path}'.");
        }
    }
}

/// <summary>
/// The promotion policy gatekeeper: a candidate change set is accepted WHOLE or rejected WHOLE
/// — there is no silent filtering of individual files, and nothing can ever be skipped by
/// passing null. The policy consumes only the immutable scanned metadata (the manifest and the
/// per-entry <see cref="ScannedEntry.ContentMayContainSecret"/> flag that was bound to the
/// exact staged bytes at ingestion time); it never re-opens mutable workspace paths.
///
/// Always rejected (and impossible to allowlist):
///  - anything under `.git` or `.tenninety` (any casing) and their descendants;
///  - the fixed Tenninety operation/control/result/sandbox-metadata names (`tenninety-*` and
///    `tenninety.*` filenames);
///  - unsafe, absolute, escaping or non-canonical paths (re-checked defensively);
///  - special entries — the scanner already proves every entry is a regular file.
///
/// Sensitive paths (rejected unless the EXACT case-sensitive path is allowlisted): the
/// families listed in <see cref="IsSensitivePath"/> — Dockerfiles, compose files, CI
/// definitions, .gitmodules/.gitattributes, NuGet/registry/package-manager configuration,
/// Directory.Build.*/Directory.Packages.*, global.json, Paket and Gradle/Cargo/Maven
/// configuration names.
///
/// Secret material: secret-shaped FILENAMES (Core Sanitizer patterns) and the exact ingested
/// content prefix flag (high-confidence detector over the first 1 MiB of the staged bytes).
/// </summary>
public static class PromotionPolicy
{
    /// <summary>Bounded best-effort content scan budget per file, enforced at ingestion time.</summary>
    public const int MaxContentScanBytesPerFile = 1_048_576;

    public static void Evaluate(
        IReadOnlyList<CandidateChange> changes,
        PromotionPolicyOptions options,
        IReadOnlyDictionary<string, ScannedEntry> targetEntries)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(targetEntries);
        options.Validate();
        var reasons = new List<string>();

        if (changes.Count > options.MaxChangedFiles)
            reasons.Add(
                $"the candidate changes {changes.Count} files, exceeding the configured " +
                $"maximum of {options.MaxChangedFiles}.");

        var allowlist = new HashSet<string>(options.AllowSensitivePaths, StringComparer.Ordinal);
        foreach (var change in changes)
        {
            var path = change.NormalizedPath;
            if (IsAlwaysRejectedPath(path))
            {
                reasons.Add($"'{path}' is a protected Tenninety/git metadata path.");
                continue;
            }
            if (!RepositoryPathPolicy.IsSafeTreePath(path))
            {
                reasons.Add($"'{path}' is not a safe repository-relative path.");
                continue;
            }
            if (Sanitizer.IsExcludedFile(path))
            {
                reasons.Add($"'{path}' is a secret-shaped filename.");
                continue;
            }
            if (IsSensitivePath(path) && !allowlist.Contains(path))
            {
                reasons.Add(
                    $"'{path}' is a sensitive path and is not listed in the human " +
                    "allowlist (sandbox.promotion.allow_sensitive_paths).");
                continue;
            }
            if (change.Kind is GitChangeKind.Added or GitChangeKind.Modified)
            {
                if (!targetEntries.TryGetValue(path, out var target))
                {
                    // Unreachable given the scanner cross-checks, but fail closed with a
                    // controlled error instead of a bare KeyNotFoundException.
                    reasons.Add(
                        $"'{path}' has no target-tree entry to verify; the change cannot be " +
                        "trusted.");
                    continue;
                }
                if (target.ContentMayContainSecret)
                {
                    reasons.Add(
                        $"'{path}' contains likely secret material in its exact ingested bytes " +
                        "(content scan).");
                }
            }
        }

        if (reasons.Count > 0)
            throw new CandidatePolicyRejectedException(
                "the candidate patch was rejected by the promotion policy: " +
                string.Join(" | ", reasons));
    }

    /// <summary>Protected metadata that may never be promoted — and can never be allowlisted —
    /// in any casing. The disposable root `.git` directory is excluded by the scanner as agent
    /// tooling; every OTHER `.git` segment is protected here.</summary>
    public static bool IsAlwaysRejectedPath(string path)
    {
        var segments = path.Split('/');
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                              s.Equals(".tenninety", StringComparison.OrdinalIgnoreCase)))
            return true;
        var fileName = FileNameOf(path);
        return fileName.StartsWith("tenninety-", StringComparison.OrdinalIgnoreCase) ||
               fileName.StartsWith("tenninety.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Sensitive paths: rejected unless the exact CASE-SENSITIVE path is human-allowlisted.
    /// Filename recognition is case-insensitive so a differently-cased file cannot sneak
    /// through (the allowlist must then name the exact normalized spelling).
    /// </summary>
    public static bool IsSensitivePath(string path)
    {
        var segments = path.Split('/');
        var fileName = FileNameOf(path);
        if (segments[0].Equals(".github", StringComparison.OrdinalIgnoreCase) ||
            segments.Any(s => s.Equals(".circleci", StringComparison.OrdinalIgnoreCase)) ||
            segments.Any(s => s.Equals(".cargo", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (fileName.Equals(".gitmodules", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".drone.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".travis.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("azure-pipelines.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Jenkinsfile", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase) ||
            (fileName.StartsWith("compose", StringComparison.OrdinalIgnoreCase) &&
             (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
              fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))))
            return true;
        if (fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Directory.Packages.", StringComparison.OrdinalIgnoreCase))
            return true;
        if (fileName.Equals("nuget.config", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("packages.config", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".npmrc", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".yarnrc", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".yarnrc.yml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".pnpmfile.cjs", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals(".pypirc", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("pip.conf", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("pip.ini", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("settings.xml", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("gradle.properties", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("paket.", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    /// <summary>High-confidence secret detection over the exact ingested prefix (delegated to
    /// the Core sanitizer's private-key/token/azure patterns). Kept here so the scanned-entry
    /// flag and the detector live in one place.</summary>
    public static bool ContainsLikelySecret(string ingestedPrefix) =>
        Sanitizer.ContainsLikelySecret(ingestedPrefix);

    private static string FileNameOf(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }
}
