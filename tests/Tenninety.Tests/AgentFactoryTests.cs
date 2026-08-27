using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Aider;
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
    };

    [Fact]
    public void Identical_coder_and_reviewer_models_are_rejected()
    {
        // The framework can enforce distinct identifiers; operators verify the actual weights.
        var factory = new AgentFactory(LiveConfig(reviewer: "Qwen3.6-27B"));
        var ex = Assert.Throws<InvalidOperationException>(factory.CreateReviewer);
        Assert.Contains("identifiers must differ", ex.Message);
    }

    [Fact]
    public void Distinct_models_pass_and_route_to_the_llama_swap_endpoint_when_enabled()
    {
        var factory = new AgentFactory(LiveConfig(llamaSwap: true));
        var coder = Assert.IsType<AiderCoderAgent>(factory.CreateCoder());
        Assert.Equal("openai/Qwen3.6-27B", coder.ResolveModel()); // provider prefix derived from <coder>
        Assert.Equal("http://localhost:9999/v1/", factory.EndpointFor("coder")); // trailing slash keeps /v1 on relative calls
        _ = factory.CreateReviewer();
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
        var coder = Assert.IsType<AiderCoderAgent>(new AgentFactory(config).CreateCoder());
        Assert.Equal("openai/custom-coder", coder.ResolveModel());
    }

    [Fact]
    public void Mock_mode_skips_the_distinctness_rule()
    {
        var config = new TenNinetyConfig();
        config.LocalModels.Reviewer = config.LocalModels.Coder; // identical is fine in rehearsal mode
        var factory = new AgentFactory(config);
        Assert.True(factory.IsMock);
        _ = factory.CreateCoder();
        _ = factory.CreateReviewer();
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

        Assert.Throws<InvalidOperationException>(() => new AgentFactory(config).CreateReviewer());
    }

    [Theory]
    [InlineData("opencode")]
    [InlineData("pi")]
    public void Cli_coders_require_an_explicit_model_in_live_mode(string coderAgent)
    {
        var config = LiveConfig();
        config.CoderAgent = coderAgent;

        var ex = Assert.Throws<InvalidOperationException>(
            () => new AgentFactory(config).CreateCoder());

        Assert.Contains("model must be explicit", ex.Message);
    }
}

public class CoderCliAgentTests
{
    [Fact]
    public void Unknown_coder_agent_is_rejected_with_supported_list()
    {
        var config = new TenNinetyConfig { ProviderMode = "aider", CoderAgent = "claude" };
        var ex = Assert.Throws<NotSupportedException>(
            () => new AgentFactory(config).CreateCoder());
        Assert.Contains("aider, opencode, pi", ex.Message);
    }

    [Fact]
    public void Legacy_openai_compatible_mode_still_routes_to_live_agents()
    {
        var config = new TenNinetyConfig
        {
            ProviderMode = "openai-compatible",
            LocalModels = new LocalModelsConfig { Coder = "Qwen3.6-27B", Reviewer = "Devstral-24B" },
        };
        var factory = new AgentFactory(config);
        Assert.False(factory.IsMock);
        Assert.IsType<AiderCoderAgent>(factory.CreateCoder());
        _ = factory.CreateReviewer();
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
