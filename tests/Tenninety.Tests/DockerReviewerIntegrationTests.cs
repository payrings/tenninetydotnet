using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.OpenAi;
using Tenninety.Execution.Reviewing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Live Docker Reviewer gate category (Category=DockerReviewer). Opt-in contract:
/// TENNINETY_RUN_DOCKER_REVIEWER_TESTS=1 plus the prerequisites named in
/// <see cref="DockerGateTestEnv"/> (the gate preflight probes every role, so the coder/tester
/// images, model network and endpoint are required too). Without the opt-in every gated test
/// is DISCOVERED and SKIPPED with the precise prerequisite message; once opted in, malformed
/// prerequisites FAIL through pure validation BEFORE any Docker/network use.
///
/// The positive gate test runs the REAL offline Docker Reviewer session driven by a
/// deterministic scripted host-side model client: read → test → temporary edit → final
/// verdict. It proves the verdict is accepted only after guest removal and that ALL reviewer
/// guest writes are discarded (never promoted).
/// </summary>
[Trait("Category", "DockerReviewer")]
public sealed class DockerReviewerIntegrationTests : IDisposable
{
    private readonly TempDir _managedRoot = new();

    public void Dispose() => _managedRoot.Dispose();

    [Fact]
    public void Opted_in_malformed_reviewer_image_fails_before_any_docker_use()
    {
        var previousImages = DockerGateTestEnv.SetPlaceholderImages();
        Environment.SetEnvironmentVariable("TENNINETY_REVIEWER_TEST_IMAGE", "latest");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root));
            Assert.Contains("TENNINETY_REVIEWER_TEST_IMAGE", ex.Message);
        }
        finally
        {
            DockerGateTestEnv.RestoreImages(previousImages);
        }
    }

    [DockerReviewerFact]
    [Trait("Category", "DockerReviewer")]
    public async Task Live_reviewer_gate_drives_actions_discards_writes_and_proves_removal()
    {
        using var repo = new TestGitRepo();
        repo.WriteFile(".gitignore", ".tenninety/\n");
        repo.WriteFile("README.md", "reviewed\n");
        repo.Commit("baseline");
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");
        var config = LiveConfig();

        // Deterministic scripted host-side model client: read, test, temporary edit, verdict.
        var scripted = new ScriptedReviewerChat(
            "{\"action\":\"run\",\"command\":\"cat /workspace/README.md\"}",
            "{\"action\":\"run\",\"command\":\"echo reviewer-temp > /workspace/reviewer-tmp.txt\"}",
            "{\"action\":\"final\",\"verdict\":\"PASS\",\"reasons\":[]}");
        var gate = new SandboxReviewerGate(repo.Git, config, scripted, "reviewer-model", log: null);

        var result = await gate.ReviewAsync(new ReviewerRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackage = new WorkPackage
            {
                Id = "WP-001",
                Title = "Live reviewer gate",
                Goal = "Scripted review",
                Directives = ["Explore then verdict"],
                AcceptanceCriteria = ["Guest writes discarded"],
            },
            Attempt = 1,
        });

        Assert.True(result.Passed, string.Join("; ", result.Reasons));
        Assert.Equal(mainSha, result.CandidateSha);
        // Reviewer guest writes are ALWAYS discarded: the temporary edit must never reach the
        // authoritative repository, and the disposable workspace must be gone.
        Assert.False(File.Exists(Path.Combine(repo.Root, "reviewer-tmp.txt")));
        Assert.Equal(mainSha, repo.Git.HeadSha());
        Assert.True(repo.Git.IsClean());
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    private TenNinetyConfig LiveConfig() => new()
    {
        ProviderMode = "aider",
        CoderAgent = "aider",
        LocalModels = new LocalModelsConfig { Coder = "coder", Reviewer = "reviewer" },
        Sandbox = DockerGateTestEnv.BuildSandboxConfig(_managedRoot.Root),
    };

    /// <summary>Deterministic scripted host-side model client for the reviewer action loop.</summary>
    private sealed class ScriptedReviewerChat(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);

        public Task<string> CompleteAsync(
            string model, string system, string user, long maxResponseBytes, CancellationToken ct) =>
            Task.FromResult(_responses.Dequeue());
    }
}
