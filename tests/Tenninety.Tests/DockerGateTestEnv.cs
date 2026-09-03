using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Shared, pure prerequisite validation for the live Docker role/end-to-end categories.
///
/// Gating contract (mirrors <see cref="DockerFactAttribute"/>):
///  - every function here is PURE: it validates strings only and performs NO Docker or
///    network access, so an opted-in run with a malformed/missing prerequisite fails BEFORE
///    any Docker/network use (never skips);
///  - image identity is only resolved later through the PRODUCTION typed
///    <see cref="DockerTestHelper.ResolveCategoryImageAsync"/> which never pulls.
///
/// Documented environment variables:
///  - per-category opt-ins: TENNINETY_RUN_DOCKER_CODER_TESTS / _REVIEWER_TESTS /
///    _TESTER_TESTS / _RESTORE_TESTS / _E2E_TESTS, each exactly "1";
///  - role images: TENNINETY_CODER_TEST_IMAGE / TENNINETY_REVIEWER_TEST_IMAGE /
///    TENNINETY_TESTER_TEST_IMAGE — exact sha256:&lt;64 lowercase hex&gt; local image IDs;
///  - network/endpoint: TENNINETY_TEST_MODEL_NETWORK (pre-existing, defaults to
///    "tenninety-coder-model") and TENNINETY_CODER_TEST_MODEL_ENDPOINT (container-reachable,
///    defaults to "http://coder-model:8000/v1" — the production config defaults);
///  - Restore operator contract: TENNINETY_RESTORE_TEST_NETWORK,
///    TENNINETY_RESTORE_TEST_PROXY_URL, TENNINETY_RESTORE_TEST_FEEDS (comma-separated),
///    TENNINETY_RESTORE_TEST_QUOTA_BYTES, TENNINETY_RESTORE_TEST_QUOTA_ID,
///    TENNINETY_RESTORE_TEST_FIREWALL_PROFILE, TENNINETY_RESTORE_TEST_EXPIRES_UTC (round-trip
///    UTC), TENNINETY_RESTORE_TEST_OPERATOR_ACK=1.
/// </summary>
public static class DockerGateTestEnv
{
    public const string DefaultModelNetwork = "tenninety-coder-model";
    public const string DefaultModelEndpoint = "http://coder-model:8000/v1";

    public static string OptIn(string variable) =>
        Environment.GetEnvironmentVariable(variable) ?? "";

    /// <summary>Format-valid placeholder image ID (not a real image — these tests are PURE and
    /// never touch Docker).</summary>
    public static string PlaceholderImage(char hex) =>
        "sha256:" + new string(hex, 64);

    /// <summary>Sets the three role image variables to format-valid placeholders so a test can
    /// isolate ONE specific prerequisite failure. Returns the previous values for restoration.</summary>
    public static (string? Coder, string? Reviewer, string? Tester) SetPlaceholderImages()
    {
        var previous = (
            Environment.GetEnvironmentVariable("TENNINETY_CODER_TEST_IMAGE"),
            Environment.GetEnvironmentVariable("TENNINETY_REVIEWER_TEST_IMAGE"),
            Environment.GetEnvironmentVariable("TENNINETY_TESTER_TEST_IMAGE"));
        Environment.SetEnvironmentVariable("TENNINETY_CODER_TEST_IMAGE", PlaceholderImage('a'));
        Environment.SetEnvironmentVariable("TENNINETY_REVIEWER_TEST_IMAGE", PlaceholderImage('b'));
        Environment.SetEnvironmentVariable("TENNINETY_TESTER_TEST_IMAGE", PlaceholderImage('c'));
        return previous;
    }

    public static void RestoreImages(
        (string? Coder, string? Reviewer, string? Tester) previous)
    {
        Environment.SetEnvironmentVariable("TENNINETY_CODER_TEST_IMAGE", previous.Coder);
        Environment.SetEnvironmentVariable("TENNINETY_REVIEWER_TEST_IMAGE", previous.Reviewer);
        Environment.SetEnvironmentVariable("TENNINETY_TESTER_TEST_IMAGE", previous.Tester);
    }

    /// <summary>Sets a VALID complete Restore operator contract (placeholder values only — these
    /// tests never touch Docker). Returns the previous values for restoration.</summary>
    public static Dictionary<string, string?> SetRestoreContractPlaceholders()
    {
        var keys = new[]
        {
            "TENNINETY_RESTORE_TEST_NETWORK",
            "TENNINETY_RESTORE_TEST_PROXY_URL",
            "TENNINETY_RESTORE_TEST_FEEDS",
            "TENNINETY_RESTORE_TEST_QUOTA_BYTES",
            "TENNINETY_RESTORE_TEST_QUOTA_ID",
            "TENNINETY_RESTORE_TEST_FIREWALL_PROFILE",
            "TENNINETY_RESTORE_TEST_EXPIRES_UTC",
            "TENNINETY_RESTORE_TEST_OPERATOR_ACK",
            "TENNINETY_RESTORE_TEST_NETWORK_ID",
        };
        var previous = keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_NETWORK", "tenninety-restore-test");
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_PROXY_URL", "http://restore-proxy:3128");
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_FEEDS", "https://api.nuget.org/v3/index.json");
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_QUOTA_BYTES", (8L * 1024 * 1024 * 1024).ToString());
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_QUOTA_ID", "quota-1");
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_FIREWALL_PROFILE", "restore-profile");
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_EXPIRES_UTC", "2099-01-01T00:00:00.0000000Z");
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_OPERATOR_ACK", "1");
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_NETWORK_ID", new string('d', 64));
        return previous;
    }

    public static void RestoreContract(Dictionary<string, string?> previous)
    {
        foreach (var (key, value) in previous)
            Environment.SetEnvironmentVariable(key, value);
    }

    /// <summary>Exact 'sha256:<64 lowercase hex>' local image ID syntax. No Docker access.</summary>
    public static string RequireImageId(string? value, string variable)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"{variable} is required once the category is opted in: provide an exact " +
                "sha256:<64 lowercase hex> local image ID that already exists on this daemon.");
        if (!value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value.Length != 71 ||
            !value[7..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new InvalidOperationException(
                $"{variable} must be an exact 'sha256:<64 lowercase hex>' local image ID.");
        return value;
    }

    /// <summary>Validated non-reserved Docker network name (production rule). No Docker access.</summary>
    public static string ModelNetwork()
    {
        var value = Environment.GetEnvironmentVariable("TENNINETY_TEST_MODEL_NETWORK");
        if (string.IsNullOrWhiteSpace(value)) value = DefaultModelNetwork;
        if (!SandboxConfig.IsValidDockerNetworkName(value))
            throw new InvalidOperationException(
                "TENNINETY_TEST_MODEL_NETWORK must be a permitted, pre-existing Docker network " +
                "name (reserved networks host/bridge/none/default and malformed names are " +
                "rejected).");
        return value;
    }

    /// <summary>Container-reachable absolute http(s) endpoint without embedded credentials and
    /// without loopback hosts. No Docker/network access.</summary>
    public static string ModelEndpoint()
    {
        var value = Environment.GetEnvironmentVariable("TENNINETY_CODER_TEST_MODEL_ENDPOINT");
        if (string.IsNullOrWhiteSpace(value)) value = DefaultModelEndpoint;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            throw new InvalidOperationException(
                "TENNINETY_CODER_TEST_MODEL_ENDPOINT must be an absolute http(s) URL without " +
                "embedded credentials.");
        if (IsSelfReferencing(uri.Host))
            throw new InvalidOperationException(
                "TENNINETY_CODER_TEST_MODEL_ENDPOINT must not be a loopback/localhost host: " +
                "it is resolved from inside the coder container.");
        return value;
    }

    private static bool IsSelfReferencing(string host)
    {
        var trimmed = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        return trimmed == "localhost" || trimmed.EndsWith(".localhost", StringComparison.Ordinal) ||
               System.Net.IPAddress.TryParse(trimmed, out var ip) &&
               (System.Net.IPAddress.IsLoopback(ip) ||
                ip.Equals(System.Net.IPAddress.Any) ||
                ip.Equals(System.Net.IPAddress.IPv6Any));
    }

    /// <summary>Builds the live gate <see cref="SandboxConfig"/> from the validated environment.
    /// No Docker access.</summary>
    public static SandboxConfig BuildSandboxConfig(string workspaceRoot)
    {
        var coder = RequireImageId(
            Environment.GetEnvironmentVariable("TENNINETY_CODER_TEST_IMAGE"),
            "TENNINETY_CODER_TEST_IMAGE");
        var reviewer = RequireImageId(
            Environment.GetEnvironmentVariable("TENNINETY_REVIEWER_TEST_IMAGE"),
            "TENNINETY_REVIEWER_TEST_IMAGE");
        var tester = RequireImageId(
            Environment.GetEnvironmentVariable("TENNINETY_TESTER_TEST_IMAGE"),
            "TENNINETY_TESTER_TEST_IMAGE");
        _ = ModelNetwork();
        _ = ModelEndpoint();
        return new SandboxConfig
        {
            WorkspaceRoot = workspaceRoot,
            ModelNetwork = ModelNetwork(),
            Roles = new SandboxRolesConfig
            {
                Coder = new CoderSandboxRoleConfig
                {
                    Image = coder,
                    ModelEndpoint = ModelEndpoint(),
                },
                Reviewer = new ReviewerSandboxRoleConfig { Image = reviewer },
                Tester = new TesterSandboxRoleConfig { Image = tester },
            },
        };
    }

    /// <summary>Validates the complete Restore operator contract and returns a config with the
    /// restricted restore phase enabled and acceptance record bound. No Docker/network access.</summary>
    public static SandboxConfig BuildRestoreConfig(string workspaceRoot)
    {
        var baseConfig = BuildSandboxConfig(workspaceRoot);
        var restore = baseConfig.Roles.Tester.Restore;

        var network = Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_NETWORK");
        if (string.IsNullOrWhiteSpace(network) || !SandboxConfig.IsValidDockerNetworkName(network))
            throw new InvalidOperationException(
                "TENNINETY_RESTORE_TEST_NETWORK must be a permitted, pre-existing restricted " +
                "network name (reserved networks and malformed names are rejected).");
        var proxy = Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_PROXY_URL");
        if (string.IsNullOrWhiteSpace(proxy) ||
            !Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri) ||
            proxyUri.Scheme is not ("http" or "https") || proxyUri.UserInfo.Length > 0 ||
            IsSelfReferencing(proxyUri.Host))
            throw new InvalidOperationException(
                "TENNINETY_RESTORE_TEST_PROXY_URL must be an absolute http(s) proxy URL without " +
                "credentials and without a loopback host.");
        var feeds = (Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_FEEDS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (feeds.Length is < 1 or > 64)
            throw new InvalidOperationException(
                "TENNINETY_RESTORE_TEST_FEEDS must contain 1-64 comma-separated https feeds.");

        if (!long.TryParse(
                Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_QUOTA_BYTES"),
                out var quotaBytes))
            throw new InvalidOperationException(
                "TENNINETY_RESTORE_TEST_QUOTA_BYTES must be an integer byte count covering " +
                "sandbox.roles.tester.restore.max_derived_mb.");
        var quotaId = Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_QUOTA_ID");
        var firewall = Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_FIREWALL_PROFILE");
        var expires = Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_EXPIRES_UTC");
        var operatorAck = Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_OPERATOR_ACK");
        if (string.IsNullOrWhiteSpace(quotaId) || string.IsNullOrWhiteSpace(firewall) ||
            string.IsNullOrWhiteSpace(expires) || operatorAck != "1" ||
            !DateTimeOffset.TryParseExact(
                expires, "O", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var expiry) ||
            expiry.Offset != TimeSpan.Zero || expiry <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException(
                "the Restore operator contract is incomplete: TENNINETY_RESTORE_TEST_QUOTA_ID, " +
                "TENNINETY_RESTORE_TEST_FIREWALL_PROFILE, a future round-trip " +
                "TENNINETY_RESTORE_TEST_EXPIRES_UTC and TENNINETY_RESTORE_TEST_OPERATOR_ACK=1 " +
                "are all required.");

        restore.Enabled = true;
        restore.NetworkName = network;
        restore.ProxyUrl = proxy;
        restore.ApprovedFeeds = feeds.ToList();

        // The acceptance record binds the quota, firewall profile, scope, expiry and the exact
        // feed policy digest (computed by PRODUCTION code). The restricted network's real
        // docker network ID is required separately (TENNINETY_RESTORE_TEST_NETWORK_ID): the
        // preflight and runtime verify the inspected network matches it.
        var networkId = Environment.GetEnvironmentVariable("TENNINETY_RESTORE_TEST_NETWORK_ID");
        if (string.IsNullOrWhiteSpace(networkId) || networkId.Length != 64 ||
            !networkId.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new InvalidOperationException(
                "TENNINETY_RESTORE_TEST_NETWORK_ID must be the 64-character lowercase hex " +
                "docker network ID of TENNINETY_RESTORE_TEST_NETWORK.");

        restore.Acceptance = new SandboxRestoreAcceptance
        {
            Version = SandboxRestoreAcceptance.CurrentVersion,
            Accepted = true,
            Repository = "", // bound per-repository by the gate test itself
            Instance = "tenninety",
            ExpiresUtc = expires,
            NetworkId = networkId,
            FirewallProfile = firewall,
            StorageQuotaId = quotaId,
            StorageQuotaBytes = quotaBytes,
            HardQuotaEnforced = true,
            OperatorAcknowledged = true,
        };
        restore.Acceptance.FeedPolicySha256 = restore.ComputeFeedPolicySha256();
        // NOTE: full production validation (ValidateStructural) runs in the test after the
        // acceptance record is bound to the disposable authoritative repository identity.
        return baseConfig;
    }
}
