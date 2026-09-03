using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Live Docker Tester gate category (Category=DockerTester). Opt-in contract:
/// TENNINETY_RUN_DOCKER_TESTER_TESTS=1 plus the prerequisites named in
/// <see cref="DockerGateTestEnv"/>; the tester image must additionally contain the .NET SDK
/// so the materialized fixture can build and test OFFLINE (the gate preflight probes every
/// role, so coder/reviewer images, the model network and endpoint are required too). Without
/// the opt-in every gated test is DISCOVERED and SKIPPED with the precise prerequisite
/// message; once opted in, malformed prerequisites FAIL through pure validation BEFORE any
/// Docker/network use.
///
/// The positive test materializes a real fixture (IsTestProject csproj, zero packages),
/// executes build + deterministic self-test offline in the hardened container, and proves
/// cleanup. The companion test proves implicit dependency restore is REJECTED: a fixture that
/// references a real package fails its offline build as an ordinary candidate failure (never
/// infrastructure), because the offline tester container cannot reach any feed.
/// </summary>
[Trait("Category", "DockerTester")]
public sealed class DockerTesterIntegrationTests : IDisposable
{
    private readonly TempDir _managedRoot = new();

    public void Dispose() => _managedRoot.Dispose();

    [Fact]
    public void Opted_in_malformed_tester_image_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        Environment.SetEnvironmentVariable("TENNINETY_TESTER_TEST_IMAGE", "ubuntu:24.04");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_TESTER_TEST_IMAGE", ex.Message);
        }
        finally
        {
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    [DockerTesterFact]
    [Trait("Category", "DockerTester")]
    public async Task Live_tester_gate_builds_and_tests_a_fixture_offline_and_proves_cleanup()
    {
        using var repo = FixtureRepo();
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");
        var config = LiveConfig();

        var gate = new SandboxTesterGate(repo.Git, config, log: null);
        var result = await gate.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(mainSha, result.CandidateSha);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [DockerTesterFact]
    [Trait("Category", "DockerTester")]
    public async Task Live_tester_gate_rejects_implicit_dependency_restore_offline()
    {
        // A fixture that references a real package must FAIL its offline build: the offline
        // tester container cannot reach a feed, so implicit restore is rejected by design.
        using var repo = new TestGitRepo();
        repo.WriteFile(".gitignore", ".tenninety/\n");
        repo.WriteFile("tests/fixture/fixture.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework><IsTestProject>true</IsTestProject>" +
            "</PropertyGroup><ItemGroup>" +
            "<PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />" +
            "</ItemGroup></Project>\n");
        repo.WriteFile("tests/fixture/Program.cs",
            "Console.WriteLine(\"fixture-tests: 1 passed\");\n");
        repo.Commit("baseline with a package reference");
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");

        var gate = new SandboxTesterGate(repo.Git, LiveConfig(), log: null);
        var result = await gate.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        // Ordinary candidate failure: the offline build could not restore, and the gate must
        // report a failed test run (never an infrastructure abort, never a pass).
        Assert.False(result.Passed);
        Assert.Equal(mainSha, result.CandidateSha);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    private TestGitRepo FixtureRepo()
    {
        var repo = new TestGitRepo();
        repo.WriteFile(".gitignore", ".tenninety/\n");
        repo.WriteFile("tests/fixture/fixture.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>" +
            "<IsTestProject>true</IsTestProject>" +
            "</PropertyGroup></Project>\n");
        repo.WriteFile("tests/fixture/Program.cs",
            "using System;\nusing System.IO;\n" +
            "if (!File.Exists(\"fixture-change.txt\")) { Console.Error.WriteLine(\"missing fixture-change.txt\"); return 1; }\n" +
            "Console.WriteLine(\"fixture-tests: 1 passed\");\nreturn 0;\n");
        repo.WriteFile("fixture-change.txt", "deterministic\n");
        repo.Commit("offline fixture");
        return repo;
    }

    private TenNinetyConfig LiveConfig() => new()
    {
        ProviderMode = "aider",
        CoderAgent = "aider",
        LocalModels = new LocalModelsConfig { Coder = "coder", Reviewer = "reviewer" },
        BuildCommand = "dotnet build tests/fixture/fixture.csproj --nologo -v q",
        TestCommand = "dotnet run --project tests/fixture/fixture.csproj --no-build",
        Sandbox = DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root),
    };
}
