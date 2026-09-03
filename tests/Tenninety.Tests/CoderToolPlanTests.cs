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

    private static string ValueAfter(IReadOnlyList<string> arguments, string flag)
    {
        var index = arguments.ToList().IndexOf(flag);
        Assert.InRange(index, 0, arguments.Count - 2);
        return arguments[index + 1];
    }
}
