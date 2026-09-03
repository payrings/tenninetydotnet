using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Live Docker Restore gate category (Category=DockerRestore). The optional restricted
/// Restore phase stays DEFAULT-DISABLED unless the COMPLETE versioned operator contract is
/// present in the environment (see <see cref="DockerGateTestEnv"/>): pre-existing restricted
/// network name + its exact docker network ID, proxy URL, approved HTTPS feeds, acknowledged
/// hard quota, firewall profile, quota id and a future round-trip expiry. The category is
/// discovered and skipped without TENNINETY_RUN_DOCKER_RESTORE_TESTS=1; once opted in, any
/// malformed or missing contract field FAILS through pure validation BEFORE any Docker or
/// network use.
///
/// The positive test proves the mechanical flow the gate owns: restricted Restore container →
/// proven removal → bounded no-follow derived-output integrity validation + digest handoff →
/// fresh OFFLINE Tester. The firewall/profile/quota enforcement itself is operator-provided
/// and external; the test never claims to prove it. The fixture is a zero-package project with
/// a committed packages.lock.json, so the locked-mode restore executes fully offline inside
/// the restricted network while still exercising the complete contract.
/// </summary>
[Trait("Category", "DockerRestore")]
public sealed class DockerRestoreIntegrationTests : IDisposable
{
    private readonly TempDir _managedRoot = new();

    public void Dispose() => _managedRoot.Dispose();

    [Fact]
    public void Opted_in_missing_proxy_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        var previousContract = DockerGateTestEnv.SetRestoreContractPlaceholders();
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_PROXY_URL", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildRestoreConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_RESTORE_TEST_PROXY_URL", ex.Message);
        }
        finally
        {
            DockerGateTestEnv.RestoreContract(previousContract);
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    [Fact]
    public void Opted_in_expired_acceptance_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        var previousContract = DockerGateTestEnv.SetRestoreContractPlaceholders();
        Environment.SetEnvironmentVariable(
            "TENNINETY_RESTORE_TEST_EXPIRES_UTC", "2020-01-01T00:00:00.0000000Z");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildRestoreConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_RESTORE_TEST_EXPIRES_UTC", ex.Message);
        }
        finally
        {
            DockerGateTestEnv.RestoreContract(previousContract);
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    [Fact]
    public void Opted_in_malformed_network_id_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        var previousContract = DockerGateTestEnv.SetRestoreContractPlaceholders();
        Environment.SetEnvironmentVariable("TENNINETY_RESTORE_TEST_NETWORK_ID", "not-hex");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildRestoreConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_RESTORE_TEST_NETWORK_ID", ex.Message);
        }
        finally
        {
            DockerGateTestEnv.RestoreContract(previousContract);
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    [DockerRestoreFact]
    [Trait("Category", "DockerRestore")]
    public async Task Live_restore_gate_runs_restricted_restore_then_fresh_offline_tester()
    {
        using var repo = FixtureRepo();
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");
        var config = LiveConfig();
        // Bind the acceptance record to this disposable authoritative repository and run the
        // full production validation (repository scope + feed policy digest + quota bounds).
        config.Sandbox.Roles.Tester.Restore.Acceptance.Repository =
            Tenninety.Execution.Sandbox.SandboxPolicy.RepositoryIdentity(repo.Root);
        config.Sandbox.ValidateStructural();

        var gate = new SandboxTesterGate(repo.Git, config, log: null);
        var result = await gate.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(mainSha, result.CandidateSha);
        // The restricted Restore must have produced a derived-output digest handed to the
        // offline Tester result.
        Assert.NotNull(result.RestoreOutputSha256);
        Assert.Matches("^[0-9a-f]{64}$", result.RestoreOutputSha256);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    private TestGitRepo FixtureRepo()
    {
        var repo = new TestGitRepo();
        repo.WriteFile(".gitignore", ".tenninety/\n");
        repo.WriteFile("tests/fixture/fixture.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>" +
            "<IsTestProject>true</IsTestProject><RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>" +
            "</PropertyGroup></Project>\n");
        // Deterministic zero-package lock file: locked-mode restore of a package-free project
        // executes fully offline and still exercises the complete restricted-restore flow.
        repo.WriteFile("tests/fixture/packages.lock.json",
            "{\n  \"version\": 1,\n  \"dependencies\": {\n    \"net10.0\": {}\n  }\n}\n");
        repo.WriteFile("tests/fixture/Program.cs",
            "using System;\nConsole.WriteLine(\"fixture-tests: 1 passed\");\n");
        repo.Commit("restore fixture");
        return repo;
    }

    private TenNinetyConfig LiveConfig() => new()
    {
        ProviderMode = "aider",
        CoderAgent = "aider",
        LocalModels = new LocalModelsConfig { Coder = "coder", Reviewer = "reviewer" },
        BuildCommand = "dotnet build tests/fixture/fixture.csproj --locked-mode --nologo -v q",
        TestCommand = "dotnet run --project tests/fixture/fixture.csproj --no-build",
        Sandbox = DockerGateTestEnv.BuildRestoreConfig(_managedRoot.Root),
    };
}
