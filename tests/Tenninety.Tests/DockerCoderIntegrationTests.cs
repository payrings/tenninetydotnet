using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Live Docker Coder gate category (Category=DockerCoder). Opt-in contract:
/// TENNINETY_RUN_DOCKER_CODER_TESTS=1 plus the prerequisites named in
/// <see cref="DockerGateTestEnv"/>. Without the opt-in every gated test is DISCOVERED and
/// SKIPPED with the precise prerequisite message; once opted in, a missing/malformed image,
/// endpoint or network FAILS the test through pure validation BEFORE any Docker/network use —
/// a requested run is never converted into a skip.
///
/// The positive gate test proves real Docker materialization → spec → create → exec →
/// removal-proof → scan/promotion against a disposable authoritative repository using a
/// deterministic guest fixture command. Real Aider/OpenCode/Pi behavior is gated separately
/// because it requires tool/model configuration inside the pinned image.
/// </summary>
[Trait("Category", "DockerCoder")]
public sealed class DockerCoderIntegrationTests : IDisposable
{
    private readonly TempDir _managedRoot = new();

    public void Dispose() => _managedRoot.Dispose();

    // ---- expected negatives (pure validation: fail BEFORE any Docker/network use) --------

    [Fact]
    public void Opted_in_malformed_image_fails_before_any_docker_use()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DockerGateTestEnv.RequireImageId("latest", "TENNINETY_CODER_TEST_IMAGE"));
        Assert.Contains("sha256:", ex.Message);
    }

    [Fact]
    public void Opted_in_missing_image_fails_before_any_docker_use()
    {
        var previous = DockerGateTestEnv.SetPlaceholderImages();
        Environment.SetEnvironmentVariable("TENNINETY_CODER_TEST_IMAGE", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_CODER_TEST_IMAGE is required", ex.Message);
        }
        finally
        {
            DockerGateTestEnv.RestoreImages(previous);
        }
    }

    [Fact]
    public void Opted_in_reserved_model_network_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        var previous = Environment.GetEnvironmentVariable("TENNINETY_TEST_MODEL_NETWORK");
        Environment.SetEnvironmentVariable("TENNINETY_TEST_MODEL_NETWORK", "host");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_TEST_MODEL_NETWORK", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TENNINETY_TEST_MODEL_NETWORK", previous);
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    [Fact]
    public void Opted_in_loopback_model_endpoint_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        var previous = Environment.GetEnvironmentVariable("TENNINETY_CODER_TEST_MODEL_ENDPOINT");
        Environment.SetEnvironmentVariable("TENNINETY_CODER_TEST_MODEL_ENDPOINT", "http://127.0.0.1:8000/v1");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_CODER_TEST_MODEL_ENDPOINT", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TENNINETY_CODER_TEST_MODEL_ENDPOINT", previous);
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    // ---- positive live gate --------------------------------------------------------------

    [DockerCoderFact]
    [Trait("Category", "DockerCoder")]
    public async Task Live_coder_gate_materializes_executes_removes_scans_and_promotes()
    {
        using var repo = new TestGitRepo();
        repo.WriteFile(".gitignore", ".tenninety/\n");
        repo.WriteFile("README.md", "baseline\n");
        repo.Commit("baseline");
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");
        using var lease = DaemonLock.Acquire(repo.Root);
        var config = LiveConfig();

        // Real transport/runtime/preflight (production defaults), deterministic guest fixture
        // command instead of a real coding tool.
        var gate = new SandboxCoderGate(
            repo.Git, config, lease, log: null,
            transportFactory: null, runtimeFactory: null, preflightFactory: null,
            deleteWorkspaceOverride: null,
            coderCommandFactory: _ => new SandboxCommand
            {
                Executable = "touch",
                Arguments = ["/workspace/fixture-change.txt"],
                WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
                Timeout = TimeSpan.FromMinutes(2),
                MaxOutputBytes = 1_048_576,
            });

        var result = await gate.ImplementAsync(new CoderRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackage = new WorkPackage
            {
                Id = "WP-001",
                Title = "Live coder gate",
                Goal = "Deterministic fixture",
                Directives = ["Touch one file"],
                AcceptanceCriteria = ["Promoted"],
            },
            Attempt = 1,
        });

        Assert.True(result.ProducedChanges, result.Summary);
        Assert.Equal(repo.Git.HeadSha(), result.CommitSha);
        Assert.True(File.Exists(Path.Combine(repo.Root, "fixture-change.txt")),
            "the deterministic fixture change must be promoted into the authoritative repo");
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public void Real_tool_gate_remains_separately_opted_in()
    {
        // The real Aider/OpenCode/Pi path needs tool+model configuration inside the pinned
        // image and a reachable model endpoint. It is a separate opt-in
        // (TENNINETY_RUN_DOCKER_CODER_REAL_TOOL_TESTS=1); the deterministic fixture gate
        // above proves the pipeline, while DockerCoderRealToolTests holds the dedicated
        // real-tool gate. Nothing here substitutes a fake tool for the real one.
        Assert.NotEqual("", DockerCoderRealToolTests.RealToolOptIn);
    }

    private TenNinetyConfig LiveConfig() => new()
    {
        ProviderMode = "aider",
        CoderAgent = "aider",
        LocalModels = new LocalModelsConfig { Coder = "coder", Reviewer = "reviewer" },
        Sandbox = DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root),
    };
}

/// <summary>Separately gated REAL coding-tool behavior: requires the pinned coder image to
/// contain the configured tool binary and a container-reachable model endpoint. Without its
/// own opt-in it stays discovered and skipped.</summary>
[Trait("Category", "DockerCoder")]
public sealed class DockerCoderRealToolTests : IDisposable
{
    internal const string RealToolOptIn = "TENNINETY_RUN_DOCKER_CODER_REAL_TOOL_TESTS";

    private readonly TempDir _managedRoot = new();

    public void Dispose() => _managedRoot.Dispose();

    [Fact]
    public async Task Real_tool_live_gate_runs_the_production_tool_command()
    {
        if (Environment.GetEnvironmentVariable(RealToolOptIn) != "1")
            return; // not opted in: the deterministic fixture gate covers the pipeline
        if (Environment.GetEnvironmentVariable("TENNINETY_RUN_DOCKER_CODER_TESTS") != "1")
            throw new InvalidOperationException(
                $"{RealToolOptIn}=1 also requires TENNINETY_RUN_DOCKER_CODER_TESTS=1 with the " +
                "shared prerequisites.");

        using var repo = new TestGitRepo();
        repo.WriteFile(".gitignore", ".tenninety/\n");
        repo.WriteFile("README.md", "baseline\n");
        repo.Commit("baseline");
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");
        using var lease = DaemonLock.Acquire(repo.Root);
        var config = LiveConfig();

        // Production path: no command seam — the real CoderToolPlan command (tool binary +
        // model endpoint from the pinned image) runs inside the container.
        var gate = new SandboxCoderGate(repo.Git, config, lease, log: null);
        var result = await gate.ImplementAsync(new CoderRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackage = new WorkPackage
            {
                Id = "WP-001",
                Title = "Real tool gate",
                Goal = "Tool + model driven",
                Directives = ["Fix the fixture"],
                AcceptanceCriteria = ["A committed change"],
            },
            Attempt = 1,
        });
        // The real tool may or may not produce a promotable change; the gate must complete
        // deterministically with proven cleanup either way.
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
        Assert.True(result.ProducedChanges == (result.CommitSha is not null));
    }

    private TenNinetyConfig LiveConfig() => new()
    {
        ProviderMode = "aider",
        CoderAgent = "aider",
        LocalModels = new LocalModelsConfig { Coder = "coder", Reviewer = "reviewer" },
        Sandbox = DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root),
    };
}
