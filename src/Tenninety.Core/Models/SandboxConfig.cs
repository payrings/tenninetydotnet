using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Tenninety.Core.Models;

/// <summary>
/// Container-isolation contract under the `sandbox` key of .tenninety/config.json.
///
/// Mode semantics (fail closed):
///  - "docker"       – required for normal live execution; every live role runs in a
///                     digest-pinned, hardened, disposable container.
///  - "unsafe-host"  – explicit compatibility opt-out that keeps the legacy host processes;
///                     it is NEVER a generated or implicit default.
/// A missing `sandbox` section deserializes to the defaults here, i.e. docker mode – it can
/// never silently select host execution. Unknown mode/runtime values throw on validation.
/// </summary>
public sealed class SandboxConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "docker";

    /// <summary>Execution adapter for docker mode. Only the typed Docker CLI adapter exists.</summary>
    [JsonPropertyName("runtime")]
    public string Runtime { get; set; } = "docker-cli";

    /// <summary>Canonical root for disposable attempt workspaces. null selects the
    /// Tenninety-managed default location beneath the system temp directory.</summary>
    [JsonPropertyName("workspace_root")]
    public string? WorkspaceRoot { get; set; }

    /// <summary>Reserved compatibility setting. Security-sensitive Docker gates always attempt
    /// proven deletion; unproven cleanup is journaled and quarantined regardless of this value.</summary>
    [JsonPropertyName("keep_failed_workspaces")]
    public bool KeepFailedWorkspaces { get; set; }

    /// <summary>Upper bound for one materialized candidate workspace, in megabytes.</summary>
    [JsonPropertyName("max_workspace_mb")]
    public int MaxWorkspaceMb { get; set; } = 4096;

    /// <summary>Pre-existing Docker network that carries coder → model traffic.</summary>
    [JsonPropertyName("model_network")]
    public string ModelNetwork { get; set; } = "tenninety-coder-model";

    [JsonPropertyName("promotion")]
    public SandboxPromotionConfig Promotion { get; set; } = new();

    [JsonPropertyName("roles")]
    public SandboxRolesConfig Roles { get; set; } = new();

    [JsonIgnore]
    public string NormalizedMode => (Mode ?? "").Trim().ToLowerInvariant() switch
    {
        "docker" => "docker",
        "unsafe-host" => "unsafe-host",
        var value => throw new InvalidOperationException(
            $"unknown sandbox mode '{value}' - supported: docker, unsafe-host. " +
            "Host execution must always be an explicit opt-in."),
    };

    [JsonIgnore]
    public string NormalizedRuntime => (Runtime ?? "").Trim().ToLowerInvariant() switch
    {
        "docker-cli" => "docker-cli",
        var value => throw new InvalidOperationException(
            $"unknown sandbox runtime '{value}' - supported: docker-cli."),
    };

    [JsonIgnore]
    public bool IsUnsafeHost => NormalizedMode == "unsafe-host";

    /// <summary>
    /// Structural validation that is safe to run whenever config is loaded: modes, runtimes,
    /// null sections, numeric bounds, promotion policy, allowlist syntax AND the full network
    /// policy contract (role networks, model network name, reviewer budget, restore boundary
    /// naming). These hold in every mode — only image and endpoint requirements stay
    /// conditional on actual live Docker selection, so mock mode still needs no images.
    /// </summary>
    public void ValidateStructural()
    {
        _ = NormalizedMode;
        _ = NormalizedRuntime;
        if (Promotion is null || Roles is null)
            throw new InvalidOperationException("config contains a null sandbox settings object.");
        if (Roles.Coder is null || Roles.Reviewer is null || Roles.Tester is null ||
            Roles.Tester.Restore is null)
            throw new InvalidOperationException("config contains a null sandbox role object.");

        if (MaxWorkspaceMb is < 16 or > 1_048_576)
            throw new InvalidOperationException(
                $"sandbox.max_workspace_mb must be within [16, 1048576] but is {MaxWorkspaceMb}.");
        if (!string.IsNullOrWhiteSpace(WorkspaceRoot) && !Path.IsPathRooted(WorkspaceRoot))
            throw new InvalidOperationException(
                "sandbox.workspace_root must be an absolute path when set.");

        // ---- network policy is structural: it holds in mock and unsafe-host mode too ----
        // The model network must always be a concrete, well-formed, non-reserved Docker
        // network name; the coder may reach its model only through it.
        if (string.IsNullOrWhiteSpace(ModelNetwork))
            throw new InvalidOperationException(
                "sandbox.model_network must be set: the coder container may reach its model " +
                "only through that pre-existing network.");
        if (!IsValidDockerNetworkName(ModelNetwork))
            throw new InvalidOperationException(
                $"sandbox.model_network '{ModelNetwork}' is not a permitted Docker network " +
                "name: it must be non-blank, well-formed, and must not be a reserved network " +
                "(host, bridge, none, default). Host networking is never permitted.");
        var coderNetwork = (Roles.Coder.Network ?? "").Trim().ToLowerInvariant();
        if (coderNetwork != "model")
            throw new InvalidOperationException(
                $"sandbox.roles.coder.network must be 'model' but is '{Roles.Coder.Network}'.");
        var reviewerNetwork = (Roles.Reviewer.Network ?? "").Trim().ToLowerInvariant();
        if (reviewerNetwork != "none")
            throw new InvalidOperationException(
                $"sandbox.roles.reviewer.network must be 'none' (offline) but is " +
                $"'{Roles.Reviewer.Network}'.");
        var testerNetwork = (Roles.Tester.Network ?? "").Trim().ToLowerInvariant();
        if (testerNetwork != "none")
            throw new InvalidOperationException(
                $"sandbox.roles.tester.network must be 'none' (offline) but is " +
                $"'{Roles.Tester.Network}'.");
        if (Roles.Reviewer is ReviewerSandboxRoleConfig reviewer &&
            reviewer.MaxActions is < 1 or > 10_000)
            throw new InvalidOperationException(
                $"sandbox.roles.reviewer.max_actions must be within [1, 10000] but is " +
                $"{reviewer.MaxActions}.");
        if (Roles.Reviewer.ActionTimeoutSeconds is < 1 or > 3600)
            throw new InvalidOperationException(
                "sandbox.roles.reviewer.action_timeout_seconds must be within [1, 3600].");
        if (Roles.Reviewer.MaxActionOutputKb is < 1 or > 65_536)
            throw new InvalidOperationException(
                "sandbox.roles.reviewer.max_action_output_kb must be within [1, 65536].");
        if (Roles.Reviewer.MaxTranscriptKb is < 16 or > 65_536)
            throw new InvalidOperationException(
                "sandbox.roles.reviewer.max_transcript_kb must be within [16, 65536].");
        if (Roles.Reviewer.MaxModelResponseKb is < 1 or > 1024)
            throw new InvalidOperationException(
                "sandbox.roles.reviewer.max_model_response_kb must be within [1, 1024].");

        Promotion.Validate();
        ValidateBounds(Roles.Coder, "sandbox.roles.coder");
        ValidateBounds(Roles.Reviewer, "sandbox.roles.reviewer");
        ValidateBounds(Roles.Tester, "sandbox.roles.tester");
        Roles.Tester.Restore.Validate();
    }

    /// <summary>
    /// Full validation for a provider mode. Live docker mode (any provider mode other than
    /// mock while the normalized sandbox mode is docker) requires digest-pinned images,
    /// valid role networks and a container-reachable coder model endpoint. Mock never touches
    /// Docker and unsafe-host explicitly keeps the legacy host path, so both skip those rules.
    /// </summary>
    public void ValidateForProvider(string providerMode)
    {
        ValidateStructural();
        if (NormalizedMode != "docker") return;
        if ((providerMode ?? "").Trim().ToLowerInvariant() == "mock") return;
        ValidateLiveDocker();
    }

    /// <summary>Hard requirements that gate any live docker execution. Network policy, model
    /// network naming and the reviewer budget are already enforced structurally; live docker
    /// additionally requires pinned images and a container-reachable model endpoint.</summary>
    public void ValidateLiveDocker()
    {
        RequirePinnedImage(Roles.Coder.Image, "sandbox.roles.coder.image");
        RequirePinnedImage(Roles.Reviewer.Image, "sandbox.roles.reviewer.image");
        RequirePinnedImage(Roles.Tester.Image, "sandbox.roles.tester.image");

        ValidateHttpEndpoint(Roles.Coder.ModelEndpoint, "sandbox.roles.coder.model_endpoint");
    }

    private static void RequirePinnedImage(string image, string field)
    {
        if (IsPinnedImageReference(image)) return;
        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException(
                $"{field} is required for live docker mode: configure a digest-pinned image " +
                "(registry reference with @sha256:…) or an exact local image ID (sha256:…).");
        throw new InvalidOperationException(
            $"{field} must be digest-pinned: '{image}' is not a registry reference with " +
            "@sha256:<64 hex> nor an exact sha256:<64 hex> local image ID. Mutable tags are " +
            "rejected so an attempt always runs the image it validated.");
    }

    /// <summary>Accepts only a registry reference containing `@sha256:<64 hex>` or an exact
    /// `sha256:<64 hex>` local image ID. Digests are lowercase hex by OCI spec.</summary>
    public static bool IsPinnedImageReference(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Any(char.IsWhiteSpace)) return false;
        if (image.StartsWith("sha256:", StringComparison.Ordinal))
            return IsSha256Hex(image["sha256:".Length..]);
        var at = image.LastIndexOf('@');
        if (at <= 0 || at == image.Length - 1) return false;
        var name = image[..at];
        if (name.Contains('@')) return false;
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '/' or '-' or '_' or ':'))
            return false;
        if (!name.Any(char.IsAsciiLetterOrDigit)) return false;
        var digest = image[(at + 1)..];
        return digest.StartsWith("sha256:", StringComparison.Ordinal) &&
               IsSha256Hex(digest["sha256:".Length..]);
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 &&
        value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>
    /// Validates an http(s) endpoint as reached FROM INSIDE a container: absolute http(s) URL,
    /// no embedded user-information credentials, and a host that is not the container itself.
    /// All loopback IPv4/IPv6 forms (127.0.0.0/8, ::1, …), the unspecified addresses, and
    /// localhost in any case or trailing-dot form are rejected.
    /// </summary>
    private static void ValidateHttpEndpoint(string? endpoint, string field)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"{field} must be an absolute http(s) URL that the container can reach, " +
                "e.g. http://coder-model:8000/v1, but the configured value has a missing or " +
                "unsupported scheme (value withheld to avoid echoing untrusted input).");
        if (uri.UserInfo.Length > 0)
            throw new InvalidOperationException(
                $"{field} must not embed user-information credentials " +
                "(user:password@…) in the URL. Pass any token out-of-band, never in the URL.");
        if (IsSelfReferencingHost(uri.Host))
            throw new InvalidOperationException(
                $"{field} '{endpoint}' refers to the container itself: loopback addresses " +
                "(127.0.0.0/8, ::1, …) and localhost inside a container never reach the host " +
                "or the model. Serve the model on the model network (e.g. " +
                "http://coder-model:8000/v1) or behind an explicitly Docker-reachable proxy.");
    }

    /// <summary>True for loopback/unspecified IPv4+IPv6 addresses (parsed, not string-matched)
    /// and for localhost in any case, with or without trailing dot, including *.localhost
    /// subdomains (RFC 6761 resolves those to loopback too).</summary>
    internal static bool IsSelfReferencingHost(string host)
    {
        var trimmed = host.Trim('[', ']');
        if (IPAddress.TryParse(trimmed, out var ip))
        {
            return IPAddress.IsLoopback(ip) ||
                   ip.Equals(IPAddress.Any) ||
                   ip.Equals(IPAddress.IPv6Any);
        }
        var normalized = trimmed.TrimEnd('.').ToLowerInvariant();
        return normalized == "localhost" ||
               normalized.EndsWith(".localhost", StringComparison.Ordinal);
    }

    internal static void ValidateBounds(SandboxRoleConfig role, string field)
    {
        if (role.Cpus is double.NaN or double.PositiveInfinity or <= 0 or > 256)
            throw new InvalidOperationException(
                $"{field}.cpus must be within (0, 256] but is {role.Cpus}.");
        if (role.MemoryMb is < 128 or > 1_048_576)
            throw new InvalidOperationException(
                $"{field}.memory_mb must be within [128, 1048576] but is {role.MemoryMb}.");
        if (role.Pids is < 1 or > 32_768)
            throw new InvalidOperationException(
                $"{field}.pids must be within [1, 32768] but is {role.Pids}.");
        if (role.TimeoutSeconds is < 1 or > 86_400)
            throw new InvalidOperationException(
                $"{field}.timeout_seconds must be within [1, 86400] but is {role.TimeoutSeconds}.");
    }

    /// <summary>
    /// Consistent Docker network-name validation for every configured network
    /// (sandbox.model_network and sandbox.roles.tester.restore.network_name): non-blank,
    /// no whitespace or control characters, Docker name syntax (leading alphanumeric, then
    /// alphanumerics/dot/underscore/dash, max 128 chars), and never a reserved or unsafe
    /// network name — host, bridge, none and default are rejected case-insensitively so host
    /// networking can never be configured by accident.
    /// </summary>
    public static bool IsValidDockerNetworkName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Any(char.IsWhiteSpace) || name.Any(char.IsControl)) return false;
        if (name.Length > 128) return false;
        if (!char.IsAsciiLetterOrDigit(name[0])) return false;
        if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')) return false;
        return !ReservedNetworkNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] ReservedNetworkNames = ["host", "bridge", "none", "default"];
}

/// <summary>Gatekeeper for promoting a validated candidate change set onto the work branch.</summary>
public sealed class SandboxPromotionConfig
{
    [JsonPropertyName("max_changed_files")]
    public int MaxChangedFiles { get; set; } = 2000;

    [JsonPropertyName("max_patch_mb")]
    public int MaxPatchMb { get; set; } = 64;

    /// <summary>Symlink and gitlink changes fail closed in v1; flipping this flag is rejected
    /// by validation until the promotion pipeline learns to validate them.</summary>
    [JsonPropertyName("allow_symlink_changes")]
    public bool AllowSymlinkChanges { get; set; }

    /// <summary>Exact normalized repository-relative paths of normally sensitive files a human
    /// explicitly allows the candidate to touch. Globs, absolute paths and traversal are invalid.</summary>
    [JsonPropertyName("allow_sensitive_paths")]
    public List<string> AllowSensitivePaths { get; set; } = new();

    public void Validate()
    {
        if (MaxChangedFiles is < 1 or > 1_000_000)
            throw new InvalidOperationException(
                $"sandbox.promotion.max_changed_files must be within [1, 1000000] but is " +
                $"{MaxChangedFiles}.");
        if (MaxPatchMb is < 1 or > 4_096)
            throw new InvalidOperationException(
                $"sandbox.promotion.max_patch_mb must be within [1, 4096] but is {MaxPatchMb}.");
        if (AllowSymlinkChanges)
            throw new InvalidOperationException(
                "sandbox.promotion.allow_symlink_changes is not supported in v1: symlink and " +
                "gitlink changes fail closed; keep the flag false.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in AllowSensitivePaths)
        {
            if (!IsExactRelativePath(path))
                throw new InvalidOperationException(
                    $"sandbox.promotion.allow_sensitive_paths entries must be exact normalized " +
                    $"repository-relative paths without globs, traversal, absolute or Windows " +
                    $"syntax, but found '{path}'.");
            if (!seen.Add(path))
                throw new InvalidOperationException(
                    $"sandbox.promotion.allow_sensitive_paths contains the duplicate entry '{path}'.");
        }
    }

    /// <summary>Strict syntax check for an exact allowlist path: forward slashes only, no
    /// absolute/drive form, no empty/'.'/'..' segments, no glob or whitespace characters.</summary>
    public static bool IsExactRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Any(char.IsWhiteSpace) || path.Contains('\0') || path.Contains(':')) return false;
        if (path.StartsWith('/') || path.Contains('\\')) return false;
        if (path.Any(c => c is '*' or '?' or '[' or ']' or '{' or '}' or '!' or '#' or '%')) return false;
        var segments = path.Split('/');
        return segments.All(s => s.Length > 0 && s != "." && s != "..") &&
               string.Equals(path, string.Join('/', segments), StringComparison.Ordinal);
    }
}

public sealed class SandboxRolesConfig
{
    [JsonPropertyName("coder")]
    public CoderSandboxRoleConfig Coder { get; set; } = new();

    [JsonPropertyName("reviewer")]
    public ReviewerSandboxRoleConfig Reviewer { get; set; } = new();

    [JsonPropertyName("tester")]
    public TesterSandboxRoleConfig Tester { get; set; } = new();
}

/// <summary>Typed, non-extensible resource description for one sandbox role. There is no
/// property that carries raw Docker arguments, mounts, devices, ports or capabilities.</summary>
public class SandboxRoleConfig
{
    /// <summary>Digest-pinned image reference or exact local image ID; blank in mock mode.</summary>
    [JsonPropertyName("image")]
    public string Image { get; set; } = "";

    [JsonPropertyName("cpus")]
    public double Cpus { get; set; } = 4.0;

    [JsonPropertyName("memory_mb")]
    public int MemoryMb { get; set; } = 8192;

    [JsonPropertyName("pids")]
    public int Pids { get; set; } = 256;

    /// <summary>"model" for the coder, "none" for reviewer/tester. Anything else fails validation.</summary>
    [JsonPropertyName("network")]
    public string Network { get; set; } = "none";

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 1200;
}

public sealed class CoderSandboxRoleConfig : SandboxRoleConfig
{
    public CoderSandboxRoleConfig()
    {
        Network = "model";
        TimeoutSeconds = 1800;
    }

    /// <summary>OpenAI-compatible endpoint as seen FROM INSIDE the coder container.</summary>
    [JsonPropertyName("model_endpoint")]
    public string ModelEndpoint { get; set; } = "http://coder-model:8000/v1";
}

public sealed class ReviewerSandboxRoleConfig : SandboxRoleConfig
{
    public ReviewerSandboxRoleConfig() => TimeoutSeconds = 1200;

    /// <summary>Budget for the host-controlled reviewer tool loop.</summary>
    [JsonPropertyName("max_actions")]
    public int MaxActions { get; set; } = 40;

    [JsonPropertyName("action_timeout_seconds")]
    public int ActionTimeoutSeconds { get; set; } = 60;

    [JsonPropertyName("max_action_output_kb")]
    public int MaxActionOutputKb { get; set; } = 256;

    [JsonPropertyName("max_transcript_kb")]
    public int MaxTranscriptKb { get; set; } = 1024;

    [JsonPropertyName("max_model_response_kb")]
    public int MaxModelResponseKb { get; set; } = 64;
}

public sealed class TesterSandboxRoleConfig : SandboxRoleConfig
{
    public TesterSandboxRoleConfig()
    {
        MemoryMb = 12288;
        Pids = 512;
        TimeoutSeconds = 1800;
    }

    [JsonPropertyName("restore")]
    public SandboxRestoreConfig Restore { get; set; } = new();
}

/// <summary>Optional separate restricted-network restore phase over the tester attempt
/// workspace. The test gate itself always runs offline.</summary>
    public sealed class SandboxRestoreConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Only the explicitly bounded "restricted" policy is valid; a general internet
    /// network must never be labelled restricted without an enforcing proxy.</summary>
    [JsonPropertyName("network")]
    public string Network { get; set; } = "restricted";

    /// <summary>Pre-existing Docker network for the restore phase; required when enabled.</summary>
    [JsonPropertyName("network_name")]
    public string NetworkName { get; set; } = "";

    /// <summary>Optional egress proxy enforced by the operator; must be an absolute http(s) URL.</summary>
    [JsonPropertyName("proxy_url")]
    public string ProxyUrl { get; set; } = "";

    /// <summary>Exact HTTPS NuGet feed endpoints encoded into the trusted generated
    /// NuGet.Config. Candidate feed configuration is ignored by the fixed restore command.</summary>
    [JsonPropertyName("approved_feeds")]
    public List<string> ApprovedFeeds { get; set; } = new();

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; set; } = 600;

    [JsonPropertyName("max_derived_mb")]
    public int MaxDerivedMb { get; set; } = 2048;

    [JsonPropertyName("max_derived_files")]
    public int MaxDerivedFiles { get; set; } = 200_000;

    [JsonPropertyName("max_derived_file_mb")]
    public int MaxDerivedFileMb { get; set; } = 512;

    [JsonPropertyName("max_derived_depth")]
    public int MaxDerivedDepth { get; set; } = 128;

    [JsonPropertyName("acceptance")]
    public SandboxRestoreAcceptance Acceptance { get; set; } = new();

    public void Validate()
    {
        // Restore policy is structural: the phase may only ever be represented as the bounded
        // restricted phase, even while it is disabled.
        if (Network.Trim().ToLowerInvariant() != "restricted")
            throw new InvalidOperationException(
                $"sandbox.roles.tester.restore.network must be 'restricted' but is '{Network}': " +
                "restore egress is only allowed through the bounded restricted phase.");
        // The configured restore network name is validated even when restore is disabled so a
        // config can never carry a reserved/unsafe network name (host, bridge, none, default).
        if (!string.IsNullOrWhiteSpace(NetworkName) && !SandboxConfig.IsValidDockerNetworkName(NetworkName))
            throw new InvalidOperationException(
                $"sandbox.roles.tester.restore.network_name '{NetworkName}' is not a permitted " +
                "Docker network name: reserved networks (host, bridge, none, default), " +
                "whitespace, control characters and malformed names are rejected. Host " +
                "networking is never permitted.");
        if (Acceptance is null || ApprovedFeeds is null)
            throw new InvalidOperationException(
                "sandbox.roles.tester.restore contains a null policy object.");
        if (TimeoutSeconds is < 1 or > 86_400)
            throw new InvalidOperationException(
                "sandbox.roles.tester.restore.timeout_seconds must be within [1, 86400].");
        if (MaxDerivedMb is < 1 or > 1_048_576 ||
            MaxDerivedFiles is < 1 or > 1_000_000 ||
            MaxDerivedFileMb is < 1 or > 1_048_576 ||
            MaxDerivedDepth is < 1 or > 1024)
            throw new InvalidOperationException(
                "sandbox.roles.tester.restore derived-output bounds are outside their supported ranges.");
        if (!Enabled) return;
        if (string.IsNullOrWhiteSpace(NetworkName))
            throw new InvalidOperationException(
                "sandbox.roles.tester.restore.network_name must name the pre-existing restricted " +
                "network when restore is enabled.");
        // The restricted proxy boundary is required configuration: without an explicit proxy
        // endpoint the "restricted" network would be indistinguishable from unrestricted
        // egress, so this contract refuses to represent it.
        if (string.IsNullOrWhiteSpace(ProxyUrl))
            throw new InvalidOperationException(
                "sandbox.roles.tester.restore.proxy_url is required when restore is enabled: " +
                "the restricted network must be represented together with the explicit proxy " +
                "boundary that enforces it. Actual network enforcement is implemented in the " +
                "sandbox runtime.");
        ValidateProxyUrl(ProxyUrl);
        if (ApprovedFeeds.Count is < 1 or > 64)
            throw new InvalidOperationException(
                "sandbox.roles.tester.restore.approved_feeds must contain 1-64 exact HTTPS feeds.");
        var uniqueFeeds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feed in ApprovedFeeds)
        {
            if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.UserInfo.Length > 0 || SandboxConfig.IsSelfReferencingHost(uri.Host) ||
                !uniqueFeeds.Add(uri.AbsoluteUri))
                throw new InvalidOperationException(
                    "sandbox.roles.tester.restore.approved_feeds contains a duplicate, " +
                    "credential-bearing, local or non-HTTPS endpoint.");
        }
        Acceptance.Validate(this);
    }

    private static void ValidateProxyUrl(string? proxyUrl)
    {
        // Same container-perspective rules as the coder endpoint: absolute http(s), no
        // embedded credentials, never a loopback/self-referencing host.
        const string field = "sandbox.roles.tester.restore.proxy_url";
        if (string.IsNullOrWhiteSpace(proxyUrl) ||
            !Uri.TryCreate(proxyUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"{field} must be an absolute http(s) URL when set, but the configured value " +
                "has a missing or unsupported scheme (value withheld to avoid echoing " +
                "untrusted input).");
        if (uri.UserInfo.Length > 0)
            throw new InvalidOperationException(
                $"{field} must not embed user-information credentials (user:password@…) in the URL.");
        if (SandboxConfig.IsSelfReferencingHost(uri.Host))
            throw new InvalidOperationException(
                $"{field} '{proxyUrl}' refers to the container itself: loopback addresses and " +
                "localhost inside the restore container never reach the proxy on the host or " +
                "the restricted network.");
    }

    public string ComputeFeedPolicySha256()
    {
        var canonical = "proxy=" + ProxyUrl + "\n" + string.Join("\n",
            ApprovedFeeds.Select(feed => new Uri(feed).AbsoluteUri)
                .Order(StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }
}

/// <summary>Versioned operator acceptance for the externally enforced restricted Restore
/// boundary. Tenninety verifies these recorded facts but does not claim to configure or prove
/// the operator's firewall, feed proxy or hard storage quota.</summary>
public sealed class SandboxRestoreAcceptance
{
    public const string CurrentVersion = "tenninety.restore.v1";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("accepted")]
    public bool Accepted { get; set; }

    [JsonPropertyName("repository")]
    public string Repository { get; set; } = "";

    [JsonPropertyName("instance")]
    public string Instance { get; set; } = "";

    [JsonPropertyName("expires_utc")]
    public string ExpiresUtc { get; set; } = "";

    [JsonPropertyName("network_id")]
    public string NetworkId { get; set; } = "";

    [JsonPropertyName("firewall_profile")]
    public string FirewallProfile { get; set; } = "";

    [JsonPropertyName("feed_policy_sha256")]
    public string FeedPolicySha256 { get; set; } = "";

    [JsonPropertyName("storage_quota_id")]
    public string StorageQuotaId { get; set; } = "";

    [JsonPropertyName("storage_quota_bytes")]
    public long StorageQuotaBytes { get; set; }

    [JsonPropertyName("hard_quota_enforced")]
    public bool HardQuotaEnforced { get; set; }

    [JsonPropertyName("operator_acknowledged")]
    public bool OperatorAcknowledged { get; set; }

    internal void Validate(SandboxRestoreConfig restore)
    {
        if (!Accepted || !OperatorAcknowledged || Version != CurrentVersion)
            throw new InvalidOperationException(
                "enabled Restore requires an accepted, operator-acknowledged " +
                $"{CurrentVersion} record.");
        if (string.IsNullOrWhiteSpace(Repository) || string.IsNullOrWhiteSpace(Instance) ||
            string.IsNullOrWhiteSpace(FirewallProfile) ||
            string.IsNullOrWhiteSpace(StorageQuotaId) ||
            new[] { Repository, Instance, FirewallProfile, StorageQuotaId }
                .Any(value => value.Length > 128 || value.Any(char.IsControl)))
            throw new InvalidOperationException(
                "Restore acceptance scope/firewall/quota identifiers must be non-blank and bounded.");
        if (!DateTimeOffset.TryParseExact(
                ExpiresUtc, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _))
            throw new InvalidOperationException(
                "Restore acceptance expires_utc must be an exact round-trip UTC timestamp.");
        if (!IsLowerHexSha256(NetworkId) || !IsLowerHexSha256(FeedPolicySha256))
            throw new InvalidOperationException(
                "Restore acceptance network_id and feed_policy_sha256 must be 64-character " +
                "lowercase hexadecimal identities.");
        var maxDerivedBytes = checked((long)restore.MaxDerivedMb * 1024 * 1024);
        if (!HardQuotaEnforced || StorageQuotaBytes < maxDerivedBytes ||
            StorageQuotaBytes > 1_099_511_627_776)
            throw new InvalidOperationException(
                "Restore requires an acknowledged hard storage quota whose capacity covers " +
                "max_derived_mb and does not exceed 1 TiB.");
        if (!string.Equals(
                FeedPolicySha256, restore.ComputeFeedPolicySha256(), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Restore acceptance feed_policy_sha256 does not match the configured proxy " +
                "and approved feed set.");
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 &&
        value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
