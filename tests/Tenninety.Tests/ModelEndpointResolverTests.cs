using Tenninety.Core.Models;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Endpoint selection is centralized in ModelEndpointResolver: these tests pin the host versus
/// container distinction, the llama-swap versus direct-model decision, trailing-slash
/// normalization and fail-closed validation for every malformed shape.
/// </summary>
public class ModelEndpointResolverTests
{
    private static TenNinetyConfig Config(bool llamaSwap = false) => new()
    {
        ProviderMode = "aider",
        UseLlamaSwap = llamaSwap,
        LlamaSwapEndpoint = "http://127.0.0.1:8080/v1",
        LlamaSwapCoderEndpoint = "http://llama-swap:8080/v1",
        LocalModelsEndpoint = "http://localhost:8000/v1",
        LocalModels = new LocalModelsConfig
        {
            Coder = "coder",
            Reviewer = "reviewer",
            CoderEndpoint = "",
            ReviewerEndpoint = "",
        },
        Aider = new AiderConfig(),
        Sandbox = new SandboxConfig
        {
            Roles = new SandboxRolesConfig
            {
                Coder = new CoderSandboxRoleConfig
                {
                    ModelEndpoint = "http://coder-model:8000/v1/",
                },
            },
        },
    };

    // ---- host perspective -----------------------------------------------------------------

    [Fact]
    public void Host_endpoint_uses_llama_swap_when_enabled_and_allows_loopback()
    {
        // Loopback is legitimate for host processes: the model server publishes 127.0.0.1.
        Assert.Equal(
            "http://127.0.0.1:8080/v1/",
            ModelEndpointResolver.ResolveHostEndpoint(Config(llamaSwap: true), "coder"));
    }

    [Fact]
    public void Host_endpoint_appends_exactly_one_trailing_slash()
    {
        var config = Config(llamaSwap: true);
        config.LlamaSwapEndpoint = "http://127.0.0.1:8080/v1/";
        Assert.Equal(
            "http://127.0.0.1:8080/v1/",
            ModelEndpointResolver.ResolveHostEndpoint(config, "reviewer"));
    }

    [Fact]
    public void Without_llama_swap_a_per_role_endpoint_wins_over_the_shared_one()
    {
        var config = Config();
        config.LocalModels.CoderEndpoint = "http://coder-direct:9000/v1";
        config.LocalModels.ReviewerEndpoint = "http://reviewer-direct:9001/v1";

        Assert.Equal(
            "http://coder-direct:9000/v1/",
            ModelEndpointResolver.ResolveHostEndpoint(config, "coder"));
        Assert.Equal(
            "http://reviewer-direct:9001/v1/",
            ModelEndpointResolver.ResolveHostEndpoint(config, "reviewer"));
        // Role matching is case-insensitive, matching the historical EndpointFor behavior.
        Assert.Equal(
            "http://reviewer-direct:9001/v1/",
            ModelEndpointResolver.ResolveHostEndpoint(config, "Reviewer"));
    }

    [Fact]
    public void Without_llama_swap_empty_per_role_endpoints_fall_back_to_the_shared_one()
    {
        var config = Config();
        Assert.Equal(
            "http://localhost:8000/v1/",
            ModelEndpointResolver.ResolveHostEndpoint(config, "reviewer"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-url")]
    [InlineData("ftp://127.0.0.1:8080/v1")]
    [InlineData("http://user:token@127.0.0.1:8080/v1")]
    public void Malformed_host_llama_swap_endpoints_fail_closed(string endpoint)
    {
        var config = Config(llamaSwap: true);
        config.LlamaSwapEndpoint = endpoint;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModelEndpointResolver.ResolveHostEndpoint(config, "reviewer"));
        Assert.Contains("llama_swap_endpoint", ex.Message);
        Assert.DoesNotContain("user:token", ex.Message);
    }

    // ---- coder container perspective ---------------------------------------------------------

    [Fact]
    public void Container_endpoint_uses_the_llama_swap_coder_endpoint_when_enabled()
    {
        Assert.Equal(
            "http://llama-swap:8080/v1",
            ModelEndpointResolver.ResolveCoderContainerEndpoint(Config(llamaSwap: true)));
    }

    [Fact]
    public void Container_endpoint_trailing_slashes_are_stripped()
    {
        var config = Config(llamaSwap: true);
        config.LlamaSwapCoderEndpoint = "http://llama-swap:8080/v1/";
        Assert.Equal(
            "http://llama-swap:8080/v1",
            ModelEndpointResolver.ResolveCoderContainerEndpoint(config));
    }

    [Fact]
    public void Without_llama_swap_the_explicit_sandbox_model_endpoint_is_preserved()
    {
        // llama-swap disabled: the configured role endpoint remains authoritative, unchanged.
        Assert.Equal(
            "http://coder-model:8000/v1",
            ModelEndpointResolver.ResolveCoderContainerEndpoint(Config()));
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080/v1")]
    [InlineData("http://localhost:8080/v1")]
    [InlineData("http://LOCALHOST:8080/v1")]
    [InlineData("http://model.localhost:8080/v1")]
    [InlineData("http://[::1]:8080/v1")]
    [InlineData("http://0.0.0.0:8080/v1")]
    public void Container_llama_swap_loopback_endpoints_refer_to_the_container_itself(string endpoint)
    {
        var config = Config(llamaSwap: true);
        config.LlamaSwapCoderEndpoint = endpoint;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModelEndpointResolver.ResolveCoderContainerEndpoint(config));
        Assert.Contains("llama_swap_coder_endpoint", ex.Message);
        Assert.Contains("refers to the container itself", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("coder-swap:8080")]
    [InlineData("ftp://coder-swap:8080/v1")]
    [InlineData("http://user:token@coder-swap:8080/v1")]
    public void Malformed_container_llama_swap_endpoints_fail_closed(string endpoint)
    {
        var config = Config(llamaSwap: true);
        config.LlamaSwapCoderEndpoint = endpoint;

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModelEndpointResolver.ResolveCoderContainerEndpoint(config));
        Assert.Contains("llama_swap_coder_endpoint", ex.Message);
        Assert.DoesNotContain("user:token", ex.Message);
    }

    [Fact]
    public void Container_sandbox_model_endpoint_still_rejects_loopback_without_llama_swap()
    {
        var config = Config();
        config.Sandbox.Roles.Coder.ModelEndpoint = "http://127.0.0.1:8080/v1";

        var ex = Assert.Throws<InvalidOperationException>(
            () => ModelEndpointResolver.ResolveCoderContainerEndpoint(config));
        Assert.Contains("sandbox.roles.coder.model_endpoint", ex.Message);
    }

    [Fact]
    public void The_same_loopback_value_is_valid_on_the_host_and_invalid_in_the_container()
    {
        var config = Config(llamaSwap: true);
        config.LlamaSwapEndpoint = "http://127.0.0.1:8080/v1";
        config.LlamaSwapCoderEndpoint = "http://127.0.0.1:8080/v1";

        // Host context accepts it...
        Assert.Equal(
            "http://127.0.0.1:8080/v1/",
            ModelEndpointResolver.ResolveHostEndpoint(config, "reviewer"));
        // ...while the container context rejects the identical value.
        Assert.Throws<InvalidOperationException>(
            () => ModelEndpointResolver.ResolveCoderContainerEndpoint(config));
    }
}
