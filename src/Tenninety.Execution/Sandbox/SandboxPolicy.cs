using System.Collections.Frozen;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Fixed security policy for every live sandbox container. These values are deliberately not
/// configurable: hardening flags, the read-only root filesystem, the tmpfs layout and the
/// closed environment/label allowlists are the same for all roles, so no configuration path
/// can weaken them.
/// </summary>
public static class SandboxPolicy
{
    /// <summary>The only container path a role workspace may be mounted at.</summary>
    public const string ContainerWorkspacePath = "/workspace";

    /// <summary>Container root filesystems are always read-only; writes go to the workspace
    /// bind mount and the bounded tmpfs mounts below.</summary>
    public const bool ReadOnlyRootFileSystem = true;

    /// <summary>Isolated HOME inside the container (no host HOME is ever mounted).</summary>
    public const string ContainerHomePath = "/home/tenninety";

    // ---- fixed tmpfs policy (defensively frozen: cannot be mutated through the surface) ----

    private static readonly System.Collections.ObjectModel.ReadOnlyCollection<TmpfsMount>
        FixedTmpfs = new List<TmpfsMount>
        {
            new("/tmp", "size=512m,nosuid,nodev,noexec"),
            new(ContainerHomePath, "size=256m,nosuid,nodev"),
        }.AsReadOnly();

    public static IReadOnlyList<TmpfsMount> FixedTmpfsMounts => FixedTmpfs;

    // ---- closed label policy: exactly the Tenninety management identity keys ----

    /// <summary>Non-secret management identity keys. Nothing else may be labelled onto a
    /// sandbox container (and no secret may travel as a label value).</summary>
    public static IReadOnlySet<string> PermittedLabelKeys { get; } = new[]
    {
        "tenninety.instance",
        "tenninety.repository",
        "tenninety.run",
        "tenninety.wp",
        "tenninety.attempt",
        "tenninety.role",
        "tenninety.candidate",
    }.ToFrozenSet(StringComparer.Ordinal);

    // ---- closed environment policies per role (exact names; no prefixes, no wildcards) ----

    private static readonly string[] CommonEnvironmentKeys =
        ["TENNINETY_WP", "TENNINETY_ATTEMPT"];

    private static readonly string[] DotnetEnvironmentKeys =
        ["DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_NOLOGO"];

    private static readonly string[] ProxyEnvironmentKeys =
        ["HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY"];

    private static readonly FrozenSet<string> CoderEnvironmentKeys =
        CommonEnvironmentKeys.Concat(
            ["OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_API_BASE"])
            .ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> ReviewerEnvironmentKeys =
        CommonEnvironmentKeys.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> TesterEnvironmentKeys =
        CommonEnvironmentKeys.Concat(DotnetEnvironmentKeys).ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> RestoreEnvironmentKeys =
        CommonEnvironmentKeys.Concat(DotnetEnvironmentKeys).Concat(ProxyEnvironmentKeys)
            .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Exact additional keys a sandbox command may set. Identity and quiet-dotnet
    /// knobs only; everything else is rejected.</summary>
    public static IReadOnlySet<string> PermittedCommandEnvironmentKeys { get; } =
        CommonEnvironmentKeys.Concat(DotnetEnvironmentKeys).ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Exact permitted environment keys for a role's container environment.
    /// HOME/PATH overrides, Docker/SSH/Git-config/LD_PRELOAD/sh-startup and credential-provider
    /// variables are deliberately absent, so they can never be set through this policy.</summary>
    public static IReadOnlySet<string> PermittedEnvironmentKeys(SandboxRole role) => role switch
    {
        SandboxRole.Coder => CoderEnvironmentKeys,
        SandboxRole.Reviewer => ReviewerEnvironmentKeys,
        SandboxRole.Tester => TesterEnvironmentKeys,
        SandboxRole.Restore => RestoreEnvironmentKeys,
        _ => FrozenSet<string>.Empty,
    };

    // ---- value hygiene bounds ----

    public const int MaxLabelValueLength = 256;
    public const int MaxEnvironmentValueLength = 1024;

    /// <summary>Policy values must be non-null, NUL-free, free of control characters and
    /// within the given length bound.</summary>
    public static bool IsSafePolicyValue(string? value, int maxLength) =>
        value is not null &&
        value.Length <= maxLength &&
        !value.Any(char.IsControl);

    /// <summary>Non-secret stable repository identity for management labels and recovery
    /// scopes. Raw host paths never cross into Docker labels.</summary>
    public static string RepositoryIdentity(string repositoryPath)
    {
        var full = Path.GetFullPath(repositoryPath);
        var canonical = TrustedPathValidation.ValidateAbsoluteShape(
            full == "/" ? full : full.TrimEnd('/'), "repository identity path");
        var name = Path.GetFileName(canonical);
        var safe = new string(name.Where(c =>
            char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
        if (safe.Length == 0) safe = "repository";
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()[..16];
        if (safe.Length > 47) safe = safe[..47];
        return safe + "-" + digest;
    }
}

/// <summary>A bounded in-container tmpfs mount (never a host bind).</summary>
public sealed record TmpfsMount(string ContainerPath, string Options);
