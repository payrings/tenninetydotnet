using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

public sealed class CoderToolPlanTests
{
    private static CoderRunContext Context() => new()
    {
        Candidate = new CandidateRevision(
            "work/WP-001", new string('a', 40), new string('b', 40)),
        WorkPackage = new WorkPackage
        {
            Id = "WP-001",
            Title = "Implement safely",
            Goal = "Keep shell metacharacters opaque: $(touch /tmp/nope); --model reviewer",
            Directives = ["Do not invoke a host shell"],
            AcceptanceCriteria = ["The exact candidate is used"],
        },
        Attempt = 2,
    };

    private static TenNinetyConfig Config(string tool) => new()
    {
        ProviderMode = "aider",
        CoderAgent = tool,
        LocalModelsEndpoint = "http://host-only.invalid/v1",
        LocalModels = new LocalModelsConfig
        {
            Coder = "coder-default",
            Reviewer = "reviewer",
        },
        Aider = new AiderConfig(),
        OpenCode = new CoderCliAgentConfig { Model = "openai/opencode-coder" },
        Pi = new CoderCliAgentConfig { Model = "openai/pi-coder" },
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

    [Fact]
    public void Aider_plan_is_exact_hermetic_and_uses_only_the_container_endpoint()
    {
        var plan = CoderToolPlan.Create(Config("aider"), Context());

        Assert.Equal("/usr/local/bin/aider", plan.Executable);
        Assert.Equal("aider", plan.Tool);
        Assert.Equal("openai/coder-default", ValueAfter(plan.Arguments, "--model"));
        Assert.Equal("http://coder-model:8000/v1", ValueAfter(plan.Arguments, "--openai-api-base"));
        Assert.Equal("/dev/null", ValueAfter(plan.Arguments, "--config"));
        Assert.Equal("/dev/null", ValueAfter(plan.Arguments, "--env-file"));
        Assert.Contains("--no-auto-commits", plan.Arguments);
        Assert.Contains("--no-check-update", plan.Arguments);
        Assert.DoesNotContain(plan.Arguments, value => value.Contains("host-only.invalid"));
        Assert.Equal("http://coder-model:8000/v1", plan.Environment["OPENAI_BASE_URL"]);
        Assert.Equal("http://coder-model:8000/v1", plan.Environment["OPENAI_API_BASE"]);
        Assert.Contains("$(touch /tmp/nope); --model reviewer", ValueAfter(plan.Arguments, "--message"));
    }

    // ---- llama-swap container endpoint routing -----------------------------------------------

    private static TenNinetyConfig LlamaSwapConfig(string tool)
    {
        var config = Config(tool);
        config.UseLlamaSwap = true;
        // The host endpoint and the disabled-mode sandbox endpoint must never leak into the
        // container plan while llama-swap owns the routing.
        config.LlamaSwapEndpoint = "http://127.0.0.1:8080/v1";
        config.LlamaSwapCoderEndpoint = "http://llama-swap:8080/v1/";
        return config;
    }

    [Fact]
    public void Aider_plan_routes_the_container_to_llama_swap_when_enabled()
    {
        var plan = CoderToolPlan.Create(LlamaSwapConfig("aider"), Context());

        Assert.Equal("http://llama-swap:8080/v1", ValueAfter(plan.Arguments, "--openai-api-base"));
        Assert.Equal("http://llama-swap:8080/v1", plan.Environment["OPENAI_BASE_URL"]);
        Assert.Equal("http://llama-swap:8080/v1", plan.Environment["OPENAI_API_BASE"]);
        Assert.DoesNotContain(plan.Arguments, value => value.Contains("coder-model"));
        Assert.DoesNotContain(plan.Environment.Values, value => value.Contains("coder-model"));
        Assert.DoesNotContain(plan.Arguments, value => value.Contains("127.0.0.1"));
        Assert.DoesNotContain(plan.Environment.Values, value => value.Contains("127.0.0.1"));
    }

    [Fact]
    public void Aider_container_loopback_llama_swap_endpoint_fails_closed()
    {
        var config = LlamaSwapConfig("aider");
        config.LlamaSwapCoderEndpoint = "http://127.0.0.1:8080/v1";

        var ex = Assert.Throws<InvalidOperationException>(
            () => CoderToolPlan.Create(config, Context()));
        Assert.Contains("llama_swap_coder_endpoint", ex.Message);
        Assert.Contains("refers to the container itself", ex.Message);
    }

    [Theory]
    [InlineData("opencode", "/usr/local/bin/opencode", "openai/opencode-coder")]
    [InlineData("pi", "/usr/local/bin/pi", "openai/pi-coder")]
    public void Cli_tool_plans_receive_the_llama_swap_endpoint_without_ownership_change(
        string tool, string executable, string model)
    {
        var plan = CoderToolPlan.Create(LlamaSwapConfig(tool), Context());

        // OpenCode and Pi keep owning their provider/model configuration: the plan changes
        // only the container environment, never the tool's own model arguments.
        Assert.Equal(executable, plan.Executable);
        Assert.Equal(model, ValueAfter(plan.Arguments, "--model"));
        Assert.Equal("http://llama-swap:8080/v1", plan.Environment["OPENAI_BASE_URL"]);
        Assert.Equal("http://llama-swap:8080/v1", plan.Environment["OPENAI_API_BASE"]);
    }

    [Theory]
    [InlineData("opencode", "/usr/local/bin/opencode", "openai/opencode-coder")]
    [InlineData("pi", "/usr/local/bin/pi", "openai/pi-coder")]
    public void Cli_tool_plans_keep_the_instruction_as_one_opaque_argument(
        string tool, string executable, string model)
    {
        var plan = CoderToolPlan.Create(Config(tool), Context());

        Assert.Equal(executable, plan.Executable);
        Assert.Equal(model, ValueAfter(plan.Arguments, "--model"));
        var instruction = plan.Arguments[^1];
        Assert.Contains("$(touch /tmp/nope); --model reviewer", instruction);
        Assert.Single(plan.Arguments, argument => argument == instruction);
        var command = plan.ToSandboxCommand(TimeSpan.FromSeconds(17));
        Assert.Equal(SandboxPolicy.ContainerWorkspacePath, command.WorkingDirectory);
        Assert.Equal(TimeSpan.FromSeconds(17), command.Timeout);
        Assert.Equal(4L * 1024 * 1024, command.MaxOutputBytes);
    }

    [Theory]
    [InlineData("aider", "--verbose")]
    [InlineData("aider", "--model reviewer")]
    [InlineData("opencode", "-m reviewer")]
    [InlineData("opencode", "--model=reviewer")]
    [InlineData("pi", "--provider hostile")]
    [InlineData("pi", "-- --model reviewer")]
    public void Docker_extra_arguments_fail_closed(string tool, string extra)
    {
        var config = Config(tool);
        if (tool == "aider") config.Aider.ExtraArgs = extra;
        else if (tool == "opencode") config.OpenCode.ExtraArgs = extra;
        else config.Pi.ExtraArgs = extra;

        var ex = Assert.Throws<InvalidOperationException>(
            () => CoderToolPlan.Create(config, Context()));

        Assert.Contains("unavailable in Docker mode", ex.Message);
    }

    [Fact]
    public void Plan_collections_are_immutable_snapshots()
    {
        var plan = CoderToolPlan.Create(Config("aider"), Context());

        Assert.Throws<NotSupportedException>(
            () => ((IList<string>)plan.Arguments).Add("--hostile"));
        Assert.Throws<NotSupportedException>(
            () => ((IDictionary<string, string>)plan.Environment).Add("HOSTILE", "1"));
    }

    [Theory]
    [InlineData("--flag \\")]
    [InlineData("--flag 'unterminated")]
    [InlineData("--flag \"unterminated")]
    public void Extra_argument_parser_rejects_unterminated_input(string raw) =>
        Assert.Throws<InvalidOperationException>(() => CoderToolPlan.ParseExtraArguments(raw));

    // ---- Pi: generated models.json custom-provider configuration ------------------------------

    [Fact]
    public void Pi_plan_writes_the_effective_endpoint_into_the_tmpfs_home()
    {
        var plan = CoderToolPlan.Create(Config("pi"), Context());

        // Aider/OpenCode never need a home setup; Pi always does in the pinned container.
        Assert.Null(CoderToolPlan.Create(Config("aider"), Context()).HomeSetupCommand);
        Assert.Null(CoderToolPlan.Create(Config("opencode"), Context()).HomeSetupCommand);

        var setup = plan.HomeSetupCommand ?? throw new InvalidOperationException("missing home setup");
        Assert.Equal("/bin/sh", setup.Executable);
        Assert.Equal(SandboxPolicy.ContainerWorkspacePath, setup.WorkingDirectory);
        Assert.Contains(CoderToolPlan.PiAgentContainerDir, setup.Arguments[1]);
        Assert.Contains(CoderToolPlan.PiModelsContainerPath, setup.Arguments[1]);

        using var document = System.Text.Json.JsonDocument.Parse(setup.StdIn!);
        var provider = document.RootElement.GetProperty("providers").GetProperty("openai");
        // The EFFECTIVE container-side endpoint is wired into Pi's supported custom-provider
        // mechanism — never through the undocumented OPENAI_BASE_URL behavior.
        Assert.Equal("http://coder-model:8000/v1", provider.GetProperty("baseUrl").GetString());
        Assert.Equal("openai-completions", provider.GetProperty("api").GetString());
        Assert.Equal("$OPENAI_API_KEY", provider.GetProperty("apiKey").GetString());
        Assert.Equal("pi-coder", provider.GetProperty("models")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void Pi_plan_routes_the_generated_provider_to_llama_swap_when_enabled()
    {
        var plan = CoderToolPlan.Create(LlamaSwapConfig("pi"), Context());

        var setup = plan.HomeSetupCommand!;
        using var document = System.Text.Json.JsonDocument.Parse(setup.StdIn!);
        var provider = document.RootElement.GetProperty("providers").GetProperty("openai");
        Assert.Equal("http://llama-swap:8080/v1", provider.GetProperty("baseUrl").GetString());
        Assert.DoesNotContain("coder-model", setup.StdIn);
        Assert.DoesNotContain("127.0.0.1", setup.StdIn);
    }

    [Fact]
    public void Pi_plan_runs_offline_and_ignores_project_local_files()
    {
        var plan = CoderToolPlan.Create(Config("pi"), Context());

        Assert.Contains("--offline", plan.Arguments);
        Assert.Contains("--no-approve", plan.Arguments);
        Assert.Contains("--no-session", plan.Arguments);
        Assert.Equal("openai/pi-coder", ValueAfter(plan.Arguments, "--model"));
    }

    [Theory]
    [InlineData("qwen3-coder", "tenninety-local", "qwen3-coder")]
    [InlineData("stub/qwen3-stub", "stub", "qwen3-stub")]
    [InlineData("openrouter/deepseek/deepseek-v3", "openrouter", "deepseek/deepseek-v3")]
    public void Pi_model_notation_is_split_into_provider_and_id(string model, string provider, string id) =>
        Assert.Equal((provider, id), CoderToolPlan.SplitPiModel(model));

    [Fact]
    public void Pi_plan_rejects_a_hostile_model_string()
    {
        var config = Config("pi");
        config.Pi = new CoderCliAgentConfig { Model = "bad/\u001b[31mmodel" };

        var ex = Assert.Throws<InvalidOperationException>(
            () => CoderToolPlan.Create(config, Context()));
        Assert.Contains("pi.model", ex.Message);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
