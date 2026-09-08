using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Role-specific live validation, Tester-only revert support, and the restricted-Restore
/// prerequisites: normal orchestration requires Coder+Reviewer+Tester (and the effective
/// Coder endpoint), while Tester-only paths require ONLY the Tester image — and Restore
/// refuses missing or inconsistent packages.lock.json files before any Docker resource.
/// </summary>
public sealed class RoleSpecificValidationTests
{
    private static readonly string CoderImage = "sha256:" + Sha('a');
    private static readonly string ReviewerImage = "sha256:" + Sha('b');
    private static readonly string TesterImage = "sha256:" + Sha('c');

    private static string Sha(char c) => new(c, 64);

    /// <summary>Blank-image parameters ("" and whitespace) leave that role image unset;
    /// everything else is an explicit override. Defaults cover every other role.</summary>
    private static SandboxConfig LiveSandbox(
        string? coderImage = null,
        string? reviewerImage = null,
        string? testerImage = null,
        string coderEndpoint = "http://coder-model:8000/v1")
    {
        var sandbox = new SandboxConfig();
        sandbox.Roles.Coder.Image = coderImage is null ? CoderImage : coderImage;
        sandbox.Roles.Reviewer.Image = reviewerImage is null ? ReviewerImage : reviewerImage;
        sandbox.Roles.Tester.Image = testerImage is null ? TesterImage : testerImage;
        sandbox.Roles.Coder.ModelEndpoint = coderEndpoint;
        return sandbox;
    }

    // ---- effective coder endpoint selection (issue 2) -------------------------------------------

    [Fact]
    public void With_llama_swap_a_blank_direct_endpoint_does_not_reject_the_configuration()
    {
        var sandbox = LiveSandbox(coderEndpoint: "   ");

        // llama-swap enabled: ONLY llama_swap_coder_endpoint is validated (container view).
        sandbox.ValidateLiveDocker(
            SandboxLiveRoles.All, useLlamaSwap: true, llamaSwapCoderEndpoint: "http://llama-swap:8080/v1");
    }

    [Fact]
    public void With_llama_swap_a_loopback_direct_endpoint_does_not_reject_the_configuration()
    {
        var sandbox = LiveSandbox(coderEndpoint: "http://127.0.0.1:8080/v1");

        sandbox.ValidateLiveDocker(
            SandboxLiveRoles.All, useLlamaSwap: true, llamaSwapCoderEndpoint: "http://llama-swap:8080/v1");
    }

    [Fact]
    public void With_llama_swap_an_invalid_effective_endpoint_still_fails_closed()
    {
        var sandbox = LiveSandbox(coderEndpoint: "http://coder-model:8000/v1");

        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateLiveDocker(
            SandboxLiveRoles.All, useLlamaSwap: true, llamaSwapCoderEndpoint: "http://localhost:8080/v1"));

        Assert.Contains("llama_swap_coder_endpoint", ex.Message);
    }

    [Fact]
    public void Without_llama_swap_the_direct_endpoint_is_validated()
    {
        var sandbox = LiveSandbox(coderEndpoint: "http://127.0.0.1:8080/v1");
        var ex = Assert.Throws<InvalidOperationException>(
            () => sandbox.ValidateLiveDocker(SandboxLiveRoles.All));
        Assert.Contains("sandbox.roles.coder.model_endpoint", ex.Message);

        var valid = LiveSandbox(coderEndpoint: "http://coder-model:8000/v1");
        valid.ValidateLiveDocker(SandboxLiveRoles.All);
    }

    [Fact]
    public void ValidateForProvider_keeps_the_direct_mode_endpoint_contract()
    {
        var sandbox = LiveSandbox(coderEndpoint: "");
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateForProvider("aider"));
        Assert.Contains("model_endpoint", ex.Message);

        // llama-swap enabled: the unused direct endpoint may stay blank/loopback.
        LiveSandbox(coderEndpoint: "http://127.0.0.1:8080/v1")
            .ValidateForProvider("aider", useLlamaSwap: true,
                llamaSwapCoderEndpoint: "http://llama-swap:8080/v1");
    }

    // ---- role-specific validation (issue 11) ------------------------------------------------------

    [Fact]
    public void Tester_only_validation_does_not_require_unrelated_role_images()
    {
        var sandbox = LiveSandbox(coderImage: null, reviewerImage: null);

        sandbox.ValidateLiveDocker(SandboxLiveRoles.Tester);
    }

    [Fact]
    public void Tester_only_validation_still_requires_the_tester_image()
    {
        var sandbox = LiveSandbox(testerImage: "");

        var ex = Assert.Throws<InvalidOperationException>(
            () => sandbox.ValidateLiveDocker(SandboxLiveRoles.Tester));

        Assert.Contains("sandbox.roles.tester.image", ex.Message);
    }

    [Fact]
    public void Full_orchestration_validation_requires_every_role_image()
    {
        var sandbox = LiveSandbox(reviewerImage: "");

        var ex = Assert.Throws<InvalidOperationException>(
            () => sandbox.ValidateLiveDocker(SandboxLiveRoles.All));

        Assert.Contains("sandbox.roles.reviewer.image", ex.Message);
    }

    [Fact]
    public void Validation_refuses_an_empty_role_selection()
    {
        var sandbox = LiveSandbox();
        Assert.Throws<InvalidOperationException>(
            () => sandbox.ValidateLiveDocker(SandboxLiveRoles.None));
    }

    // ---- agent construction paths (Coder/Reviewer/Tester/revert) -----------------------------------

    private static TenNinetyConfig ConfigFor(
        SandboxConfig sandbox, bool llamaSwap = false, string providerMode = "aider") => new()
        {
            ProviderMode = providerMode,
            UseLlamaSwap = llamaSwap,
            LlamaSwapCoderEndpoint = "http://llama-swap:8080/v1",
            Sandbox = sandbox,
        };

    [Fact]
    public void Tester_only_construction_succeeds_without_coder_or_reviewer_configuration()
    {
        var sandbox = LiveSandbox(coderImage: "", reviewerImage: "", coderEndpoint: "   ");
        var config = ConfigFor(sandbox);
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();

        var factory = new AgentFactory(config);
        var tester = factory.CreateTester(git);

        Assert.IsType<SandboxTesterGate>(tester);
    }

    [Fact]
    public void Tester_only_construction_still_rejects_a_missing_tester_image()
    {
        var sandbox = LiveSandbox(testerImage: "");
        var config = ConfigFor(sandbox);
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();

        var factory = new AgentFactory(config);

        Assert.Throws<InvalidOperationException>(() => factory.CreateTester(git));
    }

    [Fact]
    public void Coder_construction_uses_the_effective_endpoint_validation()
    {
        // Direct mode + loopback direct endpoint: rejected before any Docker resource.
        var direct = LiveSandbox(coderEndpoint: "http://127.0.0.1:8080/v1");
        var factory1 = new AgentFactory(ConfigFor(direct));
        using var repo1 = new TempDir();
        var git1 = new GitService(repo1.Root);
        git1.Init();
        Assert.Throws<InvalidOperationException>(() => factory1.CreateCoder(git1, lease: null));

        // llama-swap mode + loopback direct endpoint: accepted (the direct endpoint is unused).
        var llama = LiveSandbox(coderEndpoint: "http://127.0.0.1:8080/v1");
        var factory2 = new AgentFactory(ConfigFor(llama, llamaSwap: true));
        using var repo2 = new TempDir();
        var git2 = new GitService(repo2.Root);
        git2.Init();
        using var lease = DaemonLock.Acquire(repo2.Root);
        var coder = factory2.CreateCoder(git2, lease);
        Assert.IsType<SandboxCoderGate>(coder);
    }

    // ---- Docker preflight role filter (issue 11) ---------------------------------------------------

    [Fact]
    public async Task Tester_only_preflight_does_not_resolve_or_probe_unrelated_images()
    {
        var sandbox = LiveSandbox(coderImage: "definitely:missing@sha256:" + Sha('a'));
        using var tmp = new TempDir();
        using var repo = new TempDir();
        var fake = new PreflightFakeTransport(sandbox);
        var cli = new DockerCli(fake);

        var preflight = new DockerSandboxPreflight(
            cli, sandbox, tmp.Root, repo.Root,
            requiredRoles: SandboxLiveRoles.Tester);
        var report = await preflight.RunAsync();

        Assert.True(report.IsReady, string.Join("; ", report.Errors));
        Assert.Null(report.CoderImageId);
        Assert.Null(report.ReviewerImageId);
        Assert.Equal(PreflightFakeTransport.TesterImageId, report.TesterImageId);
        // Only the Tester probe ran (no Restore configured).
        Assert.Equal(1, fake.CreatedProbes);
        Assert.DoesNotContain(fake.Invocations, i =>
            i.Arguments.Contains("definitely:missing@sha256:" + Sha('a')));
    }

    [Fact]
    public async Task Full_preflight_still_requires_every_role_image()
    {
        var sandbox = LiveSandbox(coderImage: "definitely:missing@sha256:" + Sha('a'));
        using var tmp = new TempDir();
        using var repo = new TempDir();
        var fake = new PreflightFakeTransport(sandbox);
        var cli = new DockerCli(fake);

        var preflight = new DockerSandboxPreflight(
            cli, sandbox, tmp.Root, repo.Root, requiredRoles: SandboxLiveRoles.All);
        var report = await preflight.RunAsync();

        Assert.False(report.IsReady);
        Assert.Contains(report.Errors, e => e.Contains("the coder image could not be resolved"));
    }

    // ---- Restore prerequisites (issue 7) ------------------------------------------------------------

    [Fact]
    public void A_missing_lock_file_is_reported_with_the_owning_project()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("App.csproj"), "<Project />");

        var prerequisites = RestorePrerequisiteValidator.Validate(tmp.Root);

        Assert.Empty(prerequisites.RestoreTargets);
        Assert.Single(prerequisites.Failures);
        Assert.Contains("'App.csproj'", prerequisites.Failures[0]);
        Assert.Contains("packages.lock.json", prerequisites.Failures[0]);
        Assert.Contains("locked-mode", prerequisites.Failures[0]);
    }

    [Fact]
    public void An_inconsistent_lock_file_is_reported()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("App.csproj"), "<Project />");
        File.WriteAllText(tmp.Path("packages.lock.json"), "{ not json ");

        var prerequisites = RestorePrerequisiteValidator.Validate(tmp.Root);

        Assert.Single(prerequisites.Failures);
        Assert.Contains("not a well-formed packages.lock.json", prerequisites.Failures[0]);
    }

    [Fact]
    public void A_lock_file_without_a_dependencies_member_is_inconsistent()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("App.csproj"), "<Project />");
        File.WriteAllText(tmp.Path("packages.lock.json"), "{ \"version\": 1 }");

        var prerequisites = RestorePrerequisiteValidator.Validate(tmp.Root);

        Assert.Single(prerequisites.Failures);
        Assert.Contains("'dependencies'", prerequisites.Failures[0]);
    }

    [Fact]
    public void A_valid_lock_file_satisfies_the_prerequisites()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path("src"));
        File.WriteAllText(tmp.Path("src/App.csproj"), "<Project />");
        File.WriteAllText(tmp.Path("src/packages.lock.json"),
            "{ \"version\": 1, \"dependencies\": { \"net10.0\": {} } }");

        var prerequisites = RestorePrerequisiteValidator.Validate(tmp.Root);

        Assert.Empty(prerequisites.Failures);
        // The single discovered project is the explicit restore target.
        var target = Assert.Single(prerequisites.RestoreTargets);
        Assert.Equal("/workspace/src/App.csproj", target);
    }

    [Fact]
    public void Restore_with_no_projects_or_solutions_fails_instead_of_relying_on_the_working_directory()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Path("obj"));
        File.WriteAllText(tmp.Path("obj/gen.csproj"), "<Project />");

        var prerequisites = RestorePrerequisiteValidator.Validate(tmp.Root);

        // Reserved directories are ignored; with nothing left to restore the fixed command
        // has no explicit target, so the check fails instead of silently relying on the
        // container working directory.
        Assert.Single(prerequisites.Failures);
        Assert.Contains("no project or solution files", prerequisites.Failures[0]);
        Assert.DoesNotContain("obj/gen.csproj", prerequisites.Failures[0]);
        Assert.Empty(prerequisites.RestoreTargets);
    }

    [Fact]
    public void The_tester_environment_carries_the_documented_fixed_values()
    {
        var without = SandboxTesterGate.TesterEnvironment("WP-001", 3, restoreEnabled: false);
        Assert.DoesNotContain("NUGET_PACKAGES", without.Keys);
        Assert.Equal("WP-001", without["TENNINETY_WP"]);
        Assert.Equal("3", without["TENNINETY_ATTEMPT"]);
        // The documented quiet-dotnet knobs are actually delivered (fixed trusted values on
        // the closed allowlist), not merely permitted.
        Assert.Equal("1", without["DOTNET_CLI_TELEMETRY_OPTOUT"]);
        Assert.Equal("1", without["DOTNET_NOLOGO"]);

        var with = SandboxTesterGate.TesterEnvironment("WP-001", 3, restoreEnabled: true);
        Assert.Equal(
            SandboxPolicy.RestorePackagesContainerPath,
            with["NUGET_PACKAGES"]);
    }
}
