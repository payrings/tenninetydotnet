using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Aider;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Mock;
using Tenninety.Execution.Testing;
using Tenninety.Git;
using Xunit;

namespace Tenninety.Tests;

public class AgentFactoryTests
{
    private static TenNinetyConfig LiveConfig(
        string coder = "Qwen3.6-27B", string reviewer = "Devstral-24B", bool llamaSwap = false) => new()
    {
        ProviderMode = "aider",
        LocalModels = new LocalModelsConfig { Coder = coder, Reviewer = reviewer },
        UseLlamaSwap = llamaSwap,
        LlamaSwapEndpoint = "http://localhost:9999/v1",
        LocalModelsEndpoint = "http://localhost:8000/v1",
        Aider = new AiderConfig(),
        Sandbox = new SandboxConfig { Mode = "unsafe-host" },
    };

    [Fact]
    public void Identical_coder_and_reviewer_models_are_rejected()
    {
        // The framework can enforce distinct identifiers; operators verify the actual weights.
        var factory = new AgentFactory(LiveConfig(reviewer: "Qwen3.6-27B"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.CreateReviewer(FakeGit()));
        Assert.Contains("identifiers must differ", ex.Message);
    }

    [Fact]
    public void Distinct_models_pass_and_route_to_the_llama_swap_endpoint_when_enabled()
    {
        var factory = new AgentFactory(LiveConfig(llamaSwap: true));
        var coder = Assert.IsType<AiderCoderAgent>(factory.CreateCoder(FakeGit()));
        Assert.Equal("openai/Qwen3.6-27B", coder.ResolveModel()); // provider prefix derived from <coder>
        Assert.Equal("http://localhost:9999/v1/", factory.EndpointFor("coder")); // trailing slash keeps /v1 on relative calls
        _ = factory.CreateReviewer(FakeGit());
    }

    [Fact]
    public void Without_llama_swap_the_primary_local_endpoint_is_used()
    {
        Assert.Equal("http://localhost:8000/v1/", new AgentFactory(LiveConfig()).EndpointFor("coder"));
    }

    [Fact]
    public void Aider_model_override_is_used_verbatim()
    {
        var config = LiveConfig();
        config.Aider.Model = "openai/custom-coder";
        var coder = Assert.IsType<AiderCoderAgent>(
            new AgentFactory(config).CreateCoder(FakeGit()));
        Assert.Equal("openai/custom-coder", coder.ResolveModel());
    }

    [Fact]
    public void Mock_mode_skips_the_distinctness_rule()
    {
        var config = new TenNinetyConfig();
        config.LocalModels.Reviewer = config.LocalModels.Coder; // identical is fine in rehearsal mode
        var factory = new AgentFactory(config);
        Assert.True(factory.IsMock);
        _ = factory.CreateCoder(FakeGit());
        _ = factory.CreateReviewer(FakeGit());
    }

    [Fact]
    public void Invalid_provider_mode_is_rejected_instead_of_falling_into_live_mode()
    {
        var config = LiveConfig();
        config.ProviderMode = "mokc";

        var ex = Assert.Throws<InvalidOperationException>(() => new AgentFactory(config));

        Assert.Contains("unknown provider_mode", ex.Message);
    }

    [Fact]
    public void Provider_mode_is_trimmed_before_safe_mode_selection()
    {
        var factory = new AgentFactory(new TenNinetyConfig { ProviderMode = " Mock " });
        Assert.True(factory.IsMock);
    }

    [Fact]
    public void Identical_prefixed_models_are_rejected()
    {
        var config = LiveConfig(reviewer: "openai/same");
        config.Aider.Model = "openai/same";

        Assert.Throws<InvalidOperationException>(
            () => new AgentFactory(config).CreateReviewer(FakeGit()));
    }

    [Fact]
    public void Mutable_configuration_is_revalidated_after_an_agent_was_created()
    {
        var config = LiveConfig();
        var factory = new AgentFactory(config);
        _ = factory.CreateReviewer(FakeGit());
        config.Aider.Model = "openai/Devstral-24B";

        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateCoder(FakeGit()));

        Assert.Contains("identifiers must differ", ex.Message);
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void Cli_coders_require_an_explicit_model_in_live_mode(string coderAgent)
    {
        var config = LiveConfig();
        config.CoderAgent = coderAgent;

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AgentFactory(config).CreateCoder(FakeGit()));

        Assert.Contains("model must be explicit", ex.Message);
    }

    // ---- Phase 5A Tester selection -------------------------------------------------------------

    internal static IGitService FakeGit()
    {
        var dir = Directory.CreateTempSubdirectory("tenninety-factory-git");
        try { return new GitService(dir.FullName); }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Mock_mode_selects_the_deterministic_mock_tester()
    {
        var factory = new AgentFactory(new TenNinetyConfig());
        Assert.True(factory.IsMock);
        Assert.IsType<MockTesterAgent>(factory.CreateTester(FakeGit()));
    }

    [Fact]
    public void Mock_mode_never_runs_the_configured_shell_text_or_initializes_docker()
    {
        const string marker = "/tmp/tenninety-mock-should-never-run-marker";
        File.Delete(marker);
        var config = new TenNinetyConfig
        {
            BuildCommand = $"touch {marker}",
            TestCommand = $"touch {marker}",
            Mock = new MockBehaviorConfig { TesterFailAttempts = 0 },
            Sandbox = new SandboxConfig
            {
                Roles = new SandboxRolesConfig
                {
                    Tester = new TesterSandboxRoleConfig
                    {
                        Image = "",
                        Restore = new SandboxRestoreConfig
                        {
                            Enabled = false,
                        },
                    },
                },
            },
        };

        var tester = new AgentFactory(config).CreateTester(FakeGit());
        var result = tester.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", new string('a', 40), new string('a', 40)),
            WorkPackageId = "WP-001",
            Attempt = 1,
        }).GetAwaiter().GetResult();

        // Deterministic simulated pass, no shell ran, no Docker dependency, restore ignored.
        Assert.True(result.Passed);
        Assert.Equal(new string('a', 40), result.CandidateSha);
        Assert.False(File.Exists(marker), "mock mode must never execute the configured command");
        File.Delete(marker);
    }

    [Fact]
    public void Docker_mode_selects_the_sandbox_tester_gate_lazily()
    {
        var config = LiveConfig();
        config.Sandbox.Mode = "docker";
        config.Sandbox.Roles.Coder.Image = "sha256:" + new string('a', 64);
        config.Sandbox.Roles.Reviewer.Image = "sha256:" + new string('b', 64);
        config.Sandbox.Roles.Tester.Image = "sha256:" + new string('c', 64);

        var factory = new AgentFactory(config);
        // Selection only: no Docker executable is resolved, no temp directories are created,
        // and nothing is executed — the gate builds everything lazily at run time.
        var tester = factory.CreateTester(FakeGit());
        Assert.IsType<SandboxTesterGate>(tester);
        Assert.IsNotType<UnsafeHostTesterAgent>(tester);
    }

    [Fact]
    public void Docker_mode_requires_live_configuration_for_the_tester_path()
    {
        var config = LiveConfig();
        config.Sandbox.Mode = "docker";
        config.Sandbox.Roles.Tester.Image = "latest"; // unpinned mutable tag
        config.Sandbox.Roles.Coder.Image = "sha256:" + new string('a', 64);
        config.Sandbox.Roles.Reviewer.Image = "sha256:" + new string('b', 64);

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AgentFactory(config).CreateTester(FakeGit()));
        Assert.Contains("digest-pinned", ex.Message);
    }

    [Fact]
    public void Explicit_unsafe_host_mode_is_the_only_way_to_get_host_execution()
    {
        var config = LiveConfig();
        config.Sandbox.Mode = "unsafe-host";
        var warnings = new List<string>();

        var tester = new AgentFactory(config).CreateTester(FakeGit(), warnings.Add);
        var unsafeTester = Assert.IsType<UnsafeHostTesterAgent>(tester);

        // The prominent warning reaches the supplied logging path at construction.
        Assert.Contains(warnings, w => w.Contains("unsafe-host"));
        Assert.Contains(warnings, w => w.Contains("WARNING"));

        // Docker failures can never select this class: only the explicit mode can.
        Assert.IsNotType<SandboxTesterGate>(unsafeTester);
    }

    [Fact]
    public void An_unknown_sandbox_mode_fails_closed_for_the_tester()
    {
        var config = LiveConfig();
        config.Sandbox.Mode = "bogus-mode";

        Assert.Throws<InvalidOperationException>(
            () => new AgentFactory(config).CreateTester(FakeGit()));
    }
}

public class CoderCliAgentTests
{
    [Fact]
    public void Unknown_coder_agent_is_rejected_with_supported_list()
    {
        var config = new TenNinetyConfig { ProviderMode = "aider", CoderAgent = "claude" };
        var ex = Assert.Throws<NotSupportedException>(
            () => new AgentFactory(config).CreateCoder(AgentFactoryTests.FakeGit()));
        Assert.Contains("aider, opencode, pi", ex.Message);
    }

    [Fact]
    public void Legacy_openai_compatible_mode_still_routes_to_live_agents()
    {
        var config = new TenNinetyConfig
        {
            ProviderMode = "openai-compatible",
            LocalModels = new LocalModelsConfig { Coder = "Qwen3.6-27B", Reviewer = "Devstral-24B" },
            Sandbox = new SandboxConfig { Mode = "unsafe-host" },
        };
        var factory = new AgentFactory(config);
        Assert.False(factory.IsMock);
        Assert.IsType<AiderCoderAgent>(factory.CreateCoder(AgentFactoryTests.FakeGit()));
        _ = factory.CreateReviewer(AgentFactoryTests.FakeGit());
    }

    [Fact]
    public void Opencode_arguments_include_auto_and_model_only_when_set()
    {
        var withModel = new Execution.OpenCode.OpenCodeCoderAgent("local/qwen3.6", "", TimeSpan.FromMinutes(10));
        Assert.Equal(
            new[] { "run", "--auto", "--model", "local/qwen3.6", "do it" },
            withModel.BuildArguments("do it"));

        var defaultModel = new Execution.OpenCode.OpenCodeCoderAgent("", "--verbose", TimeSpan.FromMinutes(10));
        var args = defaultModel.BuildArguments("do it");
        Assert.DoesNotContain("--model", args);
        Assert.Contains("--verbose", args);
    }

    [Fact]
    public void Pi_arguments_are_print_mode_ephemeral_with_optional_model_and_key()
    {
        var full = new Execution.Pi.PiCoderAgent("openai/qwen3.6", "", TimeSpan.FromMinutes(10));
        Assert.Equal(
            new[] { "-p", "--no-session", "--model", "openai/qwen3.6", "do it" },
            full.BuildArguments("do it"));

        var bare = new Execution.Pi.PiCoderAgent("", "", TimeSpan.FromMinutes(10));
        var args = bare.BuildArguments("do it");
        Assert.Equal(new[] { "-p", "--no-session", "do it" }, args);
    }
}
