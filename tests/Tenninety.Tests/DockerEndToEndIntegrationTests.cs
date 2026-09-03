using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Coding;
using Tenninety.Execution.OpenAi;
using Tenninety.Execution.Reviewing;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Live Docker end-to-end category (Category=DockerEndToEnd). Opt-in contract:
/// TENNINETY_RUN_DOCKER_E2E_TESTS=1 plus the prerequisites named in
/// <see cref="DockerGateTestEnv"/>. Without the opt-in every gated test is DISCOVERED and
/// SKIPPED with the precise prerequisite message; once opted in, malformed prerequisites FAIL
/// through pure validation BEFORE any Docker/network use.
///
/// The positive test drives the deterministic three-container pipeline against one disposable
/// authoritative repository with EXACT candidate SHA propagation:
///   Coder (deterministic guest fixture command) → trusted promotion (new commit)
///   → fresh Reviewer (scripted chat PASS) → fresh Tester (offline fixture verifying the
///   promoted file). Complete cleanup is asserted at every stage; nothing depends on
///   nondeterministic model output.
/// </summary>
[Trait("Category", "DockerEndToEnd")]
public sealed class DockerEndToEndIntegrationTests : IDisposable
{
    private readonly TempDir _managedRoot = new();

    public void Dispose() => _managedRoot.Dispose();

    [Fact]
    public void Opted_in_missing_tester_image_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        Environment.SetEnvironmentVariable("TENNINETY_TESTER_TEST_IMAGE", null);
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_TESTER_TEST_IMAGE is required", ex.Message);
        }
        finally
        {
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    [DockerEndToEndFact]
    [Trait("Category", "DockerEndToEnd")]
    public async Task Live_end_to_end_drives_coder_promotion_reviewer_and_tester_with_exact_shas()
    {
        using var repo = new TestGitRepo();
        // The gates journal their attempt ownership under .tenninety/ in the repository;
        // keep it out of the authoritative tree so the worktree stays clean for promotion.
        repo.WriteFile(".gitignore", ".tenninety/\n");
        // Tester fixture committed up front: it verifies the coder-promoted file exists.
        repo.WriteFile("tests/fixture/fixture.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>" +
            "<IsTestProject>true</IsTestProject>" +
            "</PropertyGroup></Project>\n");
        repo.WriteFile("tests/fixture/Program.cs",
            "using System;\nusing System.IO;\n" +
            "if (!File.Exists(\"fixture-change.txt\")) { Console.Error.WriteLine(\"missing fixture-change.txt\"); return 1; }\n" +
            "Console.WriteLine(\"fixture-tests: 1 passed\");\nreturn 0;\n");
        repo.WriteFile("README.md", "e2e\n");
        repo.Commit("baseline");
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");
        using var lease = DaemonLock.Acquire(repo.Root);
        var config = LiveConfig();

        // ---- Coder: deterministic guest fixture command, real Docker, trusted promotion ----
        var coder = new SandboxCoderGate(
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
        var coderResult = await coder.ImplementAsync(new CoderRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackage = new WorkPackage
            {
                Id = "WP-001",
                Title = "E2E",
                Goal = "Deterministic pipeline",
                Directives = ["Touch one file"],
                AcceptanceCriteria = ["Review and tests pass"],
            },
            Attempt = 1,
        });
        Assert.True(coderResult.ProducedChanges, coderResult.Summary);
        var promotedSha = Assert.IsType<string>(coderResult.CommitSha);
        Assert.Equal(promotedSha, repo.Git.HeadSha());
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));

        // ---- Reviewer: fresh offline session, scripted chat, exact promoted SHA ------------
        var scripted = new ScriptedE2eChat(
            "{\"action\":\"run\",\"command\":\"cat /workspace/fixture-change.txt\"}",
            "{\"action\":\"final\",\"verdict\":\"PASS\",\"reasons\":[]}");
        var reviewer = new SandboxReviewerGate(repo.Git, config, scripted, "reviewer-model", log: null);
        var review = await reviewer.ReviewAsync(new ReviewerRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", promotedSha, mainSha),
            WorkPackage = new WorkPackage
            {
                Id = "WP-001",
                Title = "E2E",
                Goal = "Deterministic pipeline",
                Directives = ["Review the promotion"],
                AcceptanceCriteria = ["PASS"],
            },
            Attempt = 1,
        });
        Assert.True(review.Passed, string.Join("; ", review.Reasons));
        Assert.Equal(promotedSha, review.CandidateSha);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));

        // ---- Tester: fresh offline session against the exact promoted SHA ------------------
        var tester = new SandboxTesterGate(repo.Git, config, log: null);
        var tests = await tester.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", promotedSha, mainSha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });
        Assert.True(tests.Passed, tests.OutputTail);
        Assert.Equal(promotedSha, tests.CandidateSha);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
        Assert.Equal(promotedSha, repo.Git.HeadSha());
        Assert.True(repo.Git.IsClean());
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

    private sealed class ScriptedE2eChat(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public Task<string> CompleteAsync(
            string model, string system, string user, long maxResponseBytes, CancellationToken ct) =>
            Task.FromResult(_responses.Dequeue());
    }
}
