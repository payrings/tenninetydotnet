using Tenninety.Core.Models;
using Tenninety.Core.Stores;

namespace Tenninety.Tests;

/// <summary>
/// Phase 1 contract tests for the `sandbox` configuration section: fail-closed modes,
/// digest-pinned images, offline reviewer/tester, container-reachable coder endpoints,
/// reserved-network-name rejection, bounded resources, exact sensitive-path allowlisting,
/// and a restore phase that cannot be represented without its restricted proxy boundary.
/// There is deliberately no configuration surface for raw Docker arguments or mounts.
/// </summary>
public class SandboxConfigTests
{
    private static readonly string PinnedAiderImage =
        "ghcr.io/tenninety/coder-aider@sha256:" + Sha(1);
    private static readonly string PinnedReviewerImage =
        "ghcr.io/tenninety/reviewer@sha256:" + Sha(2);
    private static readonly string PinnedTesterImage =
        "ghcr.io/tenninety/tester-dotnet@sha256:" + Sha(3);

    private static string Sha(int seed) =>
        string.Concat(Enumerable.Range(0, 64).Select(i => (char)('a' + (i + seed) % 6)));

    private static SandboxConfig LiveDockerConfig()
    {
        var sandbox = new SandboxConfig();
        sandbox.Roles.Coder.Image = PinnedAiderImage;
        sandbox.Roles.Reviewer.Image = PinnedReviewerImage;
        sandbox.Roles.Tester.Image = PinnedTesterImage;
        return sandbox;
    }

    // ---- defaults & modes -------------------------------------------------------

    [Fact]
    public void Default_sandbox_mode_is_docker_not_unsafe_host()
    {
        var config = new TenNinetyConfig();
        Assert.Equal("docker", config.Sandbox.Mode);
        Assert.Equal("docker", config.Sandbox.NormalizedMode);
        Assert.False(config.Sandbox.IsUnsafeHost);
    }

    [Fact]
    public void Missing_sandbox_section_deserializes_to_docker_mode()
    {
        var config = JsonRoundTrip<TenNinetyConfig>("""{"provider_mode": "mock"}""");
        Assert.Equal("docker", config.Sandbox.NormalizedMode);
        config.Validate(); // must not throw
    }

    [Fact]
    public void Mode_is_normalized_with_trim_and_case()
    {
        Assert.Equal("docker", new SandboxConfig { Mode = " Docker " }.NormalizedMode);
        Assert.Equal("unsafe-host", new SandboxConfig { Mode = "UNSAFE-HOST" }.NormalizedMode);
    }

    [Fact]
    public void Unknown_sandbox_mode_fails_closed()
    {
        var config = new TenNinetyConfig { Sandbox = new SandboxConfig { Mode = "host" } };
        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("unknown sandbox mode 'host'", ex.Message);
    }

    [Fact]
    public void Unknown_sandbox_runtime_fails_closed()
    {
        var sandbox = new SandboxConfig { Runtime = "podman-cli" };
        var ex = Assert.Throws<InvalidOperationException>(sandbox.ValidateStructural);
        Assert.Contains("unknown sandbox runtime 'podman-cli'", ex.Message);
    }

    [Fact]
    public void Empty_mode_value_fails_closed()
    {
        var sandbox = new SandboxConfig { Mode = "   " };
        Assert.Throws<InvalidOperationException>(() => sandbox.ValidateStructural());
    }

    [Fact]
    public void Unsafe_host_mode_is_explicit_opt_in_and_skips_image_requirements()
    {
        var sandbox = new SandboxConfig { Mode = "unsafe-host" };
        sandbox.ValidateForProvider("aider"); // legacy host path: images/endpoints unused
        Assert.True(sandbox.IsUnsafeHost);
    }

    // ---- mock vs live -----------------------------------------------------------

    [Fact]
    public void Mock_provider_mode_validates_with_no_docker_images()
    {
        var config = new TenNinetyConfig(); // sandbox.mode defaults to docker, images blank
        config.Validate(); // structural load-time validation passes
        config.Sandbox.ValidateForProvider("mock"); // mock never touches Docker
    }

    [Fact]
    public void Live_docker_mode_rejects_blank_images()
    {
        var sandbox = new SandboxConfig(); // all images blank
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("sandbox.roles.coder.image is required", ex.Message);
    }

    [Fact]
    public void Live_docker_mode_rejects_unpinned_mutable_tags()
    {
        foreach (var image in new[] { "alpine:3.20", "ghcr.io/tenninety/coder:latest", "ubuntu" })
        {
            var sandbox = LiveDockerConfig();
            sandbox.Roles.Coder.Image = image;
            var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
            Assert.Contains("must be digest-pinned", ex.Message);
        }
    }

    [Fact]
    public void Digest_pinned_registry_references_are_accepted()
    {
        Assert.True(SandboxConfig.IsPinnedImageReference(
            "registry.example.com/team/tenninety/coder-aider@sha256:" + Sha(4)));
        Assert.True(SandboxConfig.IsPinnedImageReference("tenninety/coder@sha256:" + Sha(5)));
        Assert.False(SandboxConfig.IsPinnedImageReference("tenninety/coder@sha256:" + "ab"));
        Assert.False(SandboxConfig.IsPinnedImageReference("tenninety/coder@sha256:" + Sha(6).ToUpperInvariant()));
        Assert.False(SandboxConfig.IsPinnedImageReference("tenninety/coder@sha256:" + Sha(6) + "extra"));
        Assert.False(SandboxConfig.IsPinnedImageReference("tenninety/coder@"));
        Assert.False(SandboxConfig.IsPinnedImageReference("tenninety/coder"));
        Assert.False(SandboxConfig.IsPinnedImageReference("tenninety/coder@md5:" + Sha(6)));
        Assert.False(SandboxConfig.IsPinnedImageReference("sha256: nothex"));
        Assert.False(SandboxConfig.IsPinnedImageReference(""));
        Assert.False(SandboxConfig.IsPinnedImageReference("  sha256:" + Sha(6) + "  "));
    }

    [Fact]
    public void Exact_local_image_ids_are_accepted()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Coder.Image = "sha256:" + Sha(7);
        sandbox.Roles.Reviewer.Image = "sha256:" + Sha(8);
        sandbox.Roles.Tester.Image = "sha256:" + Sha(9);
        sandbox.ValidateForProvider("openai-compatible");
    }

    [Fact]
    public void Local_image_id_requires_full_64_hex()
    {
        Assert.True(SandboxConfig.IsPinnedImageReference("sha256:" + Sha(10)));
        Assert.False(SandboxConfig.IsPinnedImageReference("sha256:" + Sha(10)[..63]));
        Assert.False(SandboxConfig.IsPinnedImageReference("sha256:"));
    }

    // ---- networks & endpoints ---------------------------------------------------

    [Fact]
    public void Reviewer_and_tester_network_must_be_none()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Reviewer.Network = "bridge";
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("sandbox.roles.reviewer.network must be 'none'", ex.Message);

        var sandbox2 = LiveDockerConfig();
        sandbox2.Roles.Tester.Network = "model";
        var ex2 = Assert.Throws<InvalidOperationException>(() => sandbox2.ValidateForProvider("aider"));
        Assert.Contains("sandbox.roles.tester.network must be 'none'", ex2.Message);
    }

    [Fact]
    public void Coder_network_must_be_model()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Coder.Network = "none";
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("sandbox.roles.coder.network must be 'model'", ex.Message);
    }

    // ---- structural network policy (Blocker 4) ----------------------------------

    [Fact]
    public void Mock_config_rejects_unknown_role_networks_structurally()
    {
        var config = new TenNinetyConfig(); // provider_mode=mock

        config.Sandbox.Roles.Reviewer.Network = "bridge";
        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("sandbox.roles.reviewer.network must be 'none'", ex.Message);

        var config2 = new TenNinetyConfig();
        config2.Sandbox.Roles.Tester.Network = "model";
        Assert.Throws<InvalidOperationException>(config2.Validate);

        var config3 = new TenNinetyConfig();
        config3.Sandbox.Roles.Coder.Network = "none";
        Assert.Throws<InvalidOperationException>(config3.Validate);
    }

    [Fact]
    public void Unsafe_host_config_rejects_unknown_role_networks_structurally()
    {
        var config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            Sandbox = new SandboxConfig { Mode = "unsafe-host" },
        };
        config.Sandbox.Roles.Coder.Network = "none";
        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("sandbox.roles.coder.network must be 'model'", ex.Message);

        var config2 = new TenNinetyConfig
        {
            ProviderMode = "aider",
            Sandbox = new SandboxConfig { Mode = "unsafe-host" },
        };
        config2.Sandbox.Roles.Tester.Network = "bridge";
        Assert.Throws<InvalidOperationException>(config2.Validate);
    }

    [Fact]
    public void Restore_network_must_be_restricted_even_when_disabled_and_in_mock_mode()
    {
        var config = new TenNinetyConfig(); // mock, restore disabled
        config.Sandbox.Roles.Tester.Restore.Network = "host";
        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("restore.network must be 'restricted'", ex.Message);

        var sandbox = new SandboxConfig();
        sandbox.Roles.Tester.Restore.Network = "bridge";
        Assert.Throws<InvalidOperationException>(sandbox.ValidateStructural);
    }

    [Fact]
    public void Blank_model_network_fails_structural_validation_even_in_mock_mode()
    {
        var config = new TenNinetyConfig();
        config.Sandbox.ModelNetwork = "";
        Assert.Throws<InvalidOperationException>(config.Validate);

        var config2 = new TenNinetyConfig();
        config2.Sandbox.ModelNetwork = "   ";
        Assert.Throws<InvalidOperationException>(config2.Validate);
    }

    [Fact]
    public void Reviewer_max_actions_bounds_are_structural()
    {
        var config = new TenNinetyConfig(); // mock
        config.Sandbox.Roles.Reviewer.MaxActions = 0;
        Assert.Throws<InvalidOperationException>(config.Validate);

        var config2 = new TenNinetyConfig();
        config2.Sandbox.Roles.Reviewer.MaxActions = 10_001;
        Assert.Throws<InvalidOperationException>(config2.Validate);
    }

    // ---- reserved Docker network names (Task 4) ---------------------------------

    [Theory]
    [InlineData("host")]
    [InlineData("HOST")]
    [InlineData("Host")]
    [InlineData("hOsT")]
    [InlineData("bridge")]
    [InlineData("Bridge")]
    [InlineData("BRIDGE")]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("NONE")]
    [InlineData("default")]
    [InlineData("Default")]
    [InlineData("DEFAULT")]
    public void Reserved_network_names_are_rejected_case_insensitively(string name)
    {
        Assert.False(SandboxConfig.IsValidDockerNetworkName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" model ")]
    [InlineData("model\n")]
    [InlineData("tenninety\tmodel")]
    [InlineData("net\0work")]
    [InlineData("tenninety model")]
    [InlineData("-leading-dash")]
    [InlineData(".leading-dot")]
    [InlineData("_leading-underscore")]
    [InlineData("with/slash")]
    [InlineData("with:colon")]
    [InlineData("with#hash")]
    [InlineData("with$sign")]
    public void Malformed_network_names_are_rejected(string name)
    {
        Assert.False(SandboxConfig.IsValidDockerNetworkName(name));
    }

    [Theory]
    [InlineData("tenninety-coder-model")]
    [InlineData("TenNinety.Model_1")]
    [InlineData("a")]
    public void Well_formed_network_names_are_accepted(string name)
    {
        Assert.True(SandboxConfig.IsValidDockerNetworkName(name));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("bridge")]
    [InlineData("NONE")]
    [InlineData("Default")]
    public void Model_network_reserved_names_fail_config_validation(string name)
    {
        var sandbox = LiveDockerConfig();
        sandbox.ModelNetwork = name;
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("not a permitted Docker network", ex.Message);
    }

    [Fact]
    public void Model_network_reserved_names_fail_even_in_mock_mode()
    {
        var sandbox = new SandboxConfig { ModelNetwork = "Host" };
        var ex = Assert.Throws<InvalidOperationException>(sandbox.ValidateStructural);
        Assert.Contains("not a permitted Docker network", ex.Message);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("HOST")]
    [InlineData("Bridge")]
    [InlineData("none")]
    [InlineData("Default")]
    public void Restore_network_name_reserved_names_are_rejected(string name)
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Tester.Restore.NetworkName = name;
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("not a permitted Docker network", ex.Message);
    }

    [Fact]
    public void Restore_network_name_is_validated_even_when_restore_is_disabled()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Tester.Restore.Enabled = false;
        sandbox.Roles.Tester.Restore.NetworkName = "host";
        Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
    }

    // ---- coder endpoints (Task 6) -----------------------------------------------

    [Theory]
    [InlineData("http://localhost:8000/v1")]
    [InlineData("http://localhost.:8000/v1")]
    [InlineData("http://LOCALHOST:8000/v1")]
    [InlineData("http://127.0.0.1:8000/v1")]
    [InlineData("http://127.0.0.2:8000/v1")]
    [InlineData("http://127.255.1.2:8000/v1")]
    [InlineData("http://[::1]:8000/v1")]
    [InlineData("http://[::ffff:127.0.0.1]:8000/v1")]
    [InlineData("http://0.0.0.0:8000/v1")]
    [InlineData("http://[::]:8000/v1")]
    [InlineData("http://model.localhost:8000/v1")]
    [InlineData("http://user:token@coder-model:8000/v1")]
    [InlineData("http://user@coder-model:8000/v1")]
    [InlineData("coder-model:8000")]
    [InlineData("ftp://coder-model:8000/v1")]
    [InlineData("/coder-model:8000")]
    [InlineData("")]
    [InlineData("   ")]
    public void Coder_endpoints_that_cannot_reach_the_model_fail_in_docker_mode(string endpoint)
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Coder.ModelEndpoint = endpoint;
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("model_endpoint", ex.Message);
    }

    [Fact]
    public void Loopback_rejection_message_explains_the_container_itself()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Coder.ModelEndpoint = "http://127.0.0.2:8000/v1";
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("refers to the container itself", ex.Message);
    }

    [Theory]
    [InlineData("http://coder-model:8000/v1")]
    [InlineData("http://model-proxy.internal:8080/v1")]
    [InlineData("https://gateway.localnet:443/v1")]
    public void Docker_reachable_model_endpoints_are_accepted(string endpoint)
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Coder.ModelEndpoint = endpoint;
        sandbox.ValidateForProvider("aider");
    }

    // ---- resource bounds --------------------------------------------------------

    [Fact]
    public void Resource_limits_must_be_positive_and_bounded()
    {
        AssertOutOfBounds(s => s.Roles.Coder.Cpus = 0);
        AssertOutOfBounds(s => s.Roles.Coder.Cpus = 512);
        AssertOutOfBounds(s => s.Roles.Coder.MemoryMb = 0);
        AssertOutOfBounds(s => s.Roles.Coder.MemoryMb = 2_097_152);
        AssertOutOfBounds(s => s.Roles.Coder.Pids = 0);
        AssertOutOfBounds(s => s.Roles.Coder.Pids = 100_000);
        AssertOutOfBounds(s => s.Roles.Coder.TimeoutSeconds = 0);
        AssertOutOfBounds(s => s.Roles.Coder.TimeoutSeconds = 100_000);
        AssertOutOfBounds(s => s.Roles.Tester.MemoryMb = 127);
        AssertOutOfBounds(s => s.Roles.Reviewer.TimeoutSeconds = -5);
        AssertOutOfBounds(s => s.MaxWorkspaceMb = 0);
        AssertOutOfBounds(s => s.MaxWorkspaceMb = 2_097_152);
        AssertOutOfBounds(s => s.Promotion.MaxChangedFiles = 0);
        AssertOutOfBounds(s => s.Promotion.MaxPatchMb = 0);
        AssertOutOfBounds(s => s.Promotion.MaxPatchMb = 8192);
    }

    private static void AssertOutOfBounds(Action<SandboxConfig> mutate)
    {
        var sandbox = LiveDockerConfig();
        mutate(sandbox);
        Assert.Throws<InvalidOperationException>(sandbox.ValidateStructural);
    }

    // ---- sensitive-path allowlist -----------------------------------------------

    [Theory]
    [InlineData("Dockerfile")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("src/NuGet.config")]
    [InlineData("tools/Directory.Build.props")]
    public void Sensitive_allowlist_accepts_exact_safe_paths(string path)
    {
        var sandbox = LiveDockerConfig();
        sandbox.Promotion.AllowSensitivePaths.Add(path);
        sandbox.ValidateForProvider("aider");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/etc/passwd")]
    [InlineData("../secrets.env")]
    [InlineData("src/../../secrets.env")]
    [InlineData("./Dockerfile")]
    [InlineData("src//file")]
    [InlineData("src/")]
    [InlineData("a/*")]
    [InlineData("*.env")]
    [InlineData("**")]
    [InlineData("Dockerfile?")]
    [InlineData(".env[0]")]
    [InlineData("C:\\config")]
    [InlineData("a\\b")]
    [InlineData("line1\nline2")]
    [InlineData("with space")]
    [InlineData("C:/temp")]
    public void Sensitive_allowlist_rejects_globs_traversal_and_absolute_paths(string path)
    {
        var sandbox = LiveDockerConfig();
        sandbox.Promotion.AllowSensitivePaths.Add(path);
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("exact normalized repository-relative paths", ex.Message);
    }

    [Fact]
    public void Sensitive_allowlist_rejects_duplicate_entries()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Promotion.AllowSensitivePaths.AddRange(["Dockerfile", "Dockerfile"]);
        Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
    }

    [Fact]
    public void Symlink_allowance_is_rejected_in_v1()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Promotion.AllowSymlinkChanges = true;
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("not supported in v1", ex.Message);
    }

    // ---- restore (Task 6: restricted proxy boundary required) --------------------

    [Fact]
    public void Disabled_restore_without_network_name_validates_by_default()
    {
        LiveDockerConfig().ValidateForProvider("aider");
    }

    [Fact]
    public void Enabled_restore_requires_the_restricted_proxy_boundary()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Tester.Restore.Enabled = true;
        // Missing network name AND missing proxy boundary.
        var missingEverything =
            Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("network_name", missingEverything.Message);

        sandbox.Roles.Tester.Restore.NetworkName = "tenninety-restore";
        // Network name present but the restricted proxy boundary is missing.
        var missingProxy =
            Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("proxy_url is required", missingProxy.Message);

        // The complete restricted boundary representation is accepted.
        sandbox.Roles.Tester.Restore.ProxyUrl = "http://restore-proxy:3128";
        ConfigureAcceptance(sandbox.Roles.Tester.Restore);
        sandbox.ValidateForProvider("aider");
    }

    [Fact]
    public void Restore_has_no_free_form_command_property()
    {
        Assert.Null(typeof(SandboxRestoreConfig).GetProperty("Command"));
    }

    [Fact]
    public void Restore_network_must_be_restricted()
    {
        var sandbox = FullyConfiguredRestore();
        sandbox.Roles.Tester.Restore.Network = "bridge";
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("restore.network must be 'restricted'", ex.Message);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://proxy:3128")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://user:pass@restore-proxy:3128")]
    [InlineData("http://127.0.0.1:3128")]
    [InlineData("http://localhost:3128")]
    [InlineData("http://[::1]:3128")]
    public void Restore_proxy_must_be_a_container_reachable_http_url(string proxyUrl)
    {
        var sandbox = FullyConfiguredRestore();
        sandbox.Roles.Tester.Restore.ProxyUrl = proxyUrl;
        Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
    }

    private static SandboxConfig FullyConfiguredRestore()
    {
        var sandbox = LiveDockerConfig();
        sandbox.Roles.Tester.Restore.Enabled = true;
        sandbox.Roles.Tester.Restore.NetworkName = "tenninety-restore";
        sandbox.Roles.Tester.Restore.ProxyUrl = "http://restore-proxy:3128";
        ConfigureAcceptance(sandbox.Roles.Tester.Restore);
        return sandbox;
    }

    private static void ConfigureAcceptance(SandboxRestoreConfig restore)
    {
        restore.ApprovedFeeds = ["https://api.nuget.org/v3/index.json"];
        restore.Acceptance = new SandboxRestoreAcceptance
        {
            Version = SandboxRestoreAcceptance.CurrentVersion,
            Accepted = true,
            Repository = "repository",
            Instance = "tenninety",
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1).ToString("O"),
            NetworkId = new string('a', 64),
            FirewallProfile = "restore-egress-v1",
            StorageQuotaId = "restore-quota-v1",
            StorageQuotaBytes = 4L * 1024 * 1024 * 1024,
            HardQuotaEnforced = true,
            OperatorAcknowledged = true,
        };
        restore.Acceptance.FeedPolicySha256 = restore.ComputeFeedPolicySha256();
    }

    // ---- no raw Docker surface + JSON round trip ---------------------------------

    [Fact]
    public void No_config_property_accepts_raw_docker_arguments_or_mounts()
    {
        var forbidden = new[]
        {
            "mount", "volume", "device", "gpu", "port", "privileged", "capadd",
            "extraargs", "dockerarg", "sysctl", "ulimit", "bind",
        };
        var types = new[]
        {
            typeof(SandboxConfig), typeof(SandboxPromotionConfig), typeof(SandboxRolesConfig),
            typeof(SandboxRoleConfig), typeof(CoderSandboxRoleConfig),
            typeof(ReviewerSandboxRoleConfig), typeof(TesterSandboxRoleConfig),
            typeof(SandboxRestoreConfig),
        };
        foreach (var type in types)
        foreach (var property in type.GetProperties())
        {
            var name = property.Name.ToLowerInvariant();
            Assert.All(forbidden, f =>
                Assert.True(!name.Contains(f),
                    $"{type.Name}.{property.Name} must not exist: raw Docker surface is forbidden."));
        }
    }

    [Fact]
    public void Unknown_json_keys_are_ignored_and_never_reach_the_model()
    {
        const string json = """
            {
              "mode": "docker",
              "extra_docker_args": "--privileged",
              "mounts": ["/:/host"],
              "devices": ["/dev/kfd"],
              "published_ports": ["8080:80"],
              "promotion": { "allow_sensitive_paths": ["Dockerfile"], "raw_docker_flags": "x" }
            }
            """;
        var sandbox = JsonRoundTrip<SandboxConfig>(json);
        Assert.Equal("docker", sandbox.NormalizedMode);
        Assert.Single(sandbox.Promotion.AllowSensitivePaths);
        var jsonOut = Json.Serialize(sandbox);
        Assert.DoesNotContain("privileged", jsonOut);
        Assert.DoesNotContain("mounts", jsonOut);
        Assert.DoesNotContain("extra_docker_args", jsonOut);
    }

    [Fact]
    public void Sandbox_json_round_trip_preserves_all_sections()
    {
        var original = FullyConfiguredRestore();
        original.Runtime = "docker-cli";
        original.WorkspaceRoot = "/srv/tenninety-workspaces";
        original.KeepFailedWorkspaces = true;
        original.MaxWorkspaceMb = 8192;
        original.ModelNetwork = "custom-model-net";
        original.Promotion.MaxChangedFiles = 500;
        original.Promotion.MaxPatchMb = 32;
        original.Promotion.AllowSensitivePaths.Add("Dockerfile");
        original.Roles.Coder.ModelEndpoint = "http://coder-model:8000/v1";
        original.Roles.Coder.Cpus = 2.5;
        original.Roles.Reviewer.MaxActions = 25;

        var roundTripped = JsonRoundTrip<SandboxConfig>(Json.Serialize(original));

        Assert.Equal(original.Mode, roundTripped.Mode);
        Assert.Equal(original.Runtime, roundTripped.Runtime);
        Assert.Equal(original.WorkspaceRoot, roundTripped.WorkspaceRoot);
        Assert.Equal(original.KeepFailedWorkspaces, roundTripped.KeepFailedWorkspaces);
        Assert.Equal(original.MaxWorkspaceMb, roundTripped.MaxWorkspaceMb);
        Assert.Equal(original.ModelNetwork, roundTripped.ModelNetwork);
        Assert.Equal(original.Promotion.MaxChangedFiles, roundTripped.Promotion.MaxChangedFiles);
        Assert.Equal(original.Promotion.MaxPatchMb, roundTripped.Promotion.MaxPatchMb);
        Assert.Equal(original.Promotion.AllowSensitivePaths, roundTripped.Promotion.AllowSensitivePaths);
        Assert.Equal(original.Roles.Coder.Image, roundTripped.Roles.Coder.Image);
        Assert.Equal(original.Roles.Coder.Cpus, roundTripped.Roles.Coder.Cpus);
        Assert.Equal(original.Roles.Coder.ModelEndpoint, roundTripped.Roles.Coder.ModelEndpoint);
        Assert.Equal(original.Roles.Reviewer.MaxActions, roundTripped.Roles.Reviewer.MaxActions);
        Assert.Equal(original.Roles.Tester.Restore.Enabled, roundTripped.Roles.Tester.Restore.Enabled);
        Assert.Equal(original.Roles.Tester.Restore.Network, roundTripped.Roles.Tester.Restore.Network);
        Assert.Equal(original.Roles.Tester.Restore.NetworkName, roundTripped.Roles.Tester.Restore.NetworkName);
        Assert.Equal(original.Roles.Tester.Restore.ProxyUrl, roundTripped.Roles.Tester.Restore.ProxyUrl);
        Assert.Equal(original.Roles.Tester.Restore.ApprovedFeeds, roundTripped.Roles.Tester.Restore.ApprovedFeeds);
        Assert.Equal(original.Roles.Tester.Restore.Acceptance.NetworkId,
            roundTripped.Roles.Tester.Restore.Acceptance.NetworkId);
        Assert.Equal(original.Roles.Tester.MemoryMb, roundTripped.Roles.Tester.MemoryMb);
        Assert.Equal(original.Roles.Tester.Pids, roundTripped.Roles.Tester.Pids);
        roundTripped.ValidateForProvider("aider");
    }

    [Fact]
    public void TenNinetyConfig_validate_surfaces_invalid_sandbox_values()
    {
        var config = new TenNinetyConfig { Sandbox = new SandboxConfig { Mode = "permissive" } };
        Assert.Throws<InvalidOperationException>(config.Validate);

        var config2 = new TenNinetyConfig();
        config2.Sandbox.Roles.Coder.Cpus = -1;
        Assert.Throws<InvalidOperationException>(config2.Validate);
    }

    [Fact]
    public void Null_sandbox_sub_sections_fail_closed()
    {
        var config = JsonRoundTrip<TenNinetyConfig>(
            """{"sandbox": {"mode": "docker", "roles": null}}""");
        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    private static T JsonRoundTrip<T>(string json) where T : notnull =>
        Json.Deserialize<T>(json);
}
