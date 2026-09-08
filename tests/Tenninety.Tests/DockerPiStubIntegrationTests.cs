using System.Diagnostics;
using System.Text;
using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Live Docker Pi stub-endpoint integration test (Category=DockerPiStub). Proves — against a
/// REAL daemon and the REAL production coder path (no seams) — that the pinned Pi container
/// reaches Tenninety's configured container endpoint through Pi's supported custom-provider
/// mechanism (the generated <c>~/.pi/agent/models.json</c>), uses exactly the configured
/// provider/model, never needs any external provider endpoint, and completes a minimal
/// workspace-editing request. No GPU and no real model is involved.
///
/// The stub is a small deterministic OpenAI-compatible SSE server (a dependency-free node
/// script) running INSIDE a container on the model network — exactly like the production
/// llama-swap/direct-model topology, a container can reach it by the network alias, and the
/// test records every request it receives. Turn one answers with Pi's own <c>write</c> tool
/// call creating the marker file; every later turn answers with a plain text completion.
///
/// Opt-in contract: TENNINETY_RUN_DOCKER_PI_STUB_TESTS=1 plus TENNINETY_PI_TEST_IMAGE (the
/// local Pi coder image; its node runtime also hosts the stub), TENNINETY_REVIEWER_TEST_IMAGE,
/// TENNINETY_TESTER_TEST_IMAGE (the preflight probes every role image) and
/// TENNINETY_TEST_MODEL_NETWORK (a pre-existing local network). Images are never pulled.
/// Without the opt-in the test is discovered and skipped with the precise prerequisite
/// message.
/// </summary>
[Trait("Category", "DockerPiStub")]
public sealed class DockerPiStubIntegrationTests : IDisposable
{
    private const string OptIn = "TENNINETY_RUN_DOCKER_PI_STUB_TESTS";
    private const string StubModel = "stub/qwen3-stub";
    private const string StubModelId = "qwen3-stub";
    private const string EditedFileName = "pi-stub-reached.txt";
    private const string EditedFileContent = "pi reached the configured stub endpoint\n";

    private readonly TempDir _managedRoot = new();
    private string? _stubContainer;

    public void Dispose()
    {
        if (_stubContainer is not null)
            RunDocker(["rm", "-f", _stubContainer], stdin: null, timeout: 30_000);
        _managedRoot.Dispose();
    }

    [Fact]
    public void The_category_keeps_its_own_documented_opt_in() =>
        Assert.Equal("TENNINETY_RUN_DOCKER_PI_STUB_TESTS", OptIn);

    [DockerPiStubFact]
    [Trait("Category", "DockerPiStub")]
    public async Task Pi_reaches_the_configured_endpoint_uses_the_configured_model_and_edits_the_workspace()
    {
        var network = DockerGateTestEnv.ModelNetwork();
        var piImage = DockerGateTestEnv.RequireImageId(
            Environment.GetEnvironmentVariable("TENNINETY_PI_TEST_IMAGE"),
            "TENNINETY_PI_TEST_IMAGE");
        var reviewerImage = DockerGateTestEnv.RequireImageId(
            Environment.GetEnvironmentVariable("TENNINETY_REVIEWER_TEST_IMAGE"),
            "TENNINETY_REVIEWER_TEST_IMAGE");
        var testerImage = DockerGateTestEnv.RequireImageId(
            Environment.GetEnvironmentVariable("TENNINETY_TESTER_TEST_IMAGE"),
            "TENNINETY_TESTER_TEST_IMAGE");

        // Stub server container on the model network (node from the pinned Pi image; no pull).
        var stubAlias = "stub-model-" + Guid.NewGuid().ToString("N")[..10];
        _stubContainer = "tenninety-pi-stub-" + Guid.NewGuid().ToString("N")[..10];
        var stubPort = Random.Shared.Next(20000, 45000);
        RunDocker(["run", "-d", "--rm", "--name", _stubContainer,
                   "--network", network, "--network-alias", stubAlias,
                   "--entrypoint", "sleep", piImage, "infinity"],
                  stdin: null, timeout: 60_000);
        RunDocker(["exec", "-i", _stubContainer, "/bin/sh", "-c", "cat > /tmp/stub.js"],
                  stdin: StubNodeScript(stubPort), timeout: 30_000);
        RunDocker(["exec", "-d", _stubContainer, "node", "/tmp/stub.js"],
                  stdin: null, timeout: 30_000);
        Thread.Sleep(1_000); // let the stub bind its port

        var endpoint = $"http://{stubAlias}:{stubPort}/v1";

        var previousLocalKey =
            Environment.GetEnvironmentVariable("TENNINETY_LOCAL_API_KEY");
        Environment.SetEnvironmentVariable("TENNINETY_LOCAL_API_KEY", null);
        try
        {
            using var repo = new TestGitRepo();
            repo.WriteFile(".gitignore", ".tenninety/\n");
            repo.WriteFile("README.md", "baseline\n");
            repo.Commit("baseline");
            var mainSha = repo.Git.HeadSha();
            repo.Git.CreateAndCheckoutBranch("work/WP-001");
            using var lease = DaemonLock.Acquire(repo.Root);

            var config = new TenNinetyConfig
            {
                ProviderMode = "aider",
                CoderAgent = "pi",
                Pi = new CoderCliAgentConfig { Model = StubModel },
                LocalModels = new LocalModelsConfig { Coder = "coder", Reviewer = "reviewer" },
                Sandbox = new SandboxConfig
                {
                    WorkspaceRoot = _managedRoot.Root,
                    ModelNetwork = network,
                    Roles = new SandboxRolesConfig
                    {
                        Coder = new CoderSandboxRoleConfig
                        {
                            Image = piImage,
                            ModelEndpoint = endpoint,
                        },
                        Reviewer = new ReviewerSandboxRoleConfig { Image = reviewerImage },
                        Tester = new TesterSandboxRoleConfig { Image = testerImage },
                    },
                },
            };

            // Production path: no seams, no fixture command — the real CoderToolPlan command
            // (generated models.json + Pi binary) runs inside the pinned Pi container.
            var gate = new SandboxCoderGate(repo.Git, config, lease, log: null);
            var result = await gate.ImplementAsync(new CoderRunContext
            {
                Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
                WorkPackage = new WorkPackage
                {
                    Id = "WP-001",
                    Title = "Pi stub gate",
                    Goal = "Reach the configured endpoint and edit the workspace",
                    Directives = ["Create the marker file"],
                    AcceptanceCriteria = ["A committed marker file"],
                },
                Attempt = 1,
            });

            // 4. The Pi tool completed the minimal workspace-editing request and the trusted
            //    promotion carried the exact edit into the authoritative repository.
            Assert.True(result.ProducedChanges, result.Summary);
            Assert.Equal(EditedFileContent,
                File.ReadAllText(Path.Combine(repo.Root, EditedFileName)));

            // The stub recorded EVERY request Pi made: the run completed entirely on stub
            // turns, so no external provider endpoint was involved.
            var requests = ReadStubRecords();
            Assert.True(requests.Count >= 2,
                "the stub must observe at least the tool-call and the final completion turn");

            // 1+3. Pi reached the CONFIGURED container endpoint, and it is the only endpoint
            //       the run used.
            Assert.All(requests, request =>
            {
                Assert.Equal("/v1/chat/completions", request.Path);

                // 2. Every request carries the CONFIGURED provider's model id — Pi resolved
                //    the generated models.json provider/model, not a built-in provider and
                //    not the undocumented OPENAI_BASE_URL behavior.
                Assert.Equal(StubModelId, request.Model);

                // The API key came from the closed container environment ($OPENAI_API_KEY),
                // never from the generated file or the image.
                Assert.StartsWith("Bearer ", request.Authorization);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("TENNINETY_LOCAL_API_KEY", previousLocalKey);
        }
    }

    private IReadOnlyList<StubRequest> ReadStubRecords()
    {
        var (_, stdout, _) = RunDocker(
            ["exec", _stubContainer!, "cat", "/tmp/record.jsonl"],
            stdin: null, timeout: 30_000);
        return stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<StubRequest>(line)!)
            .ToList();
    }

    private sealed record StubRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("path")] string Path,
        [property: System.Text.Json.Serialization.JsonPropertyName("model")] string Model,
        [property: System.Text.Json.Serialization.JsonPropertyName("auth")] string Authorization);

    /// <summary>Dependency-free node stub: turn one answers with Pi's own <c>write</c> tool
    /// call creating the marker file, every later turn with a plain text completion. Every
    /// request (path, model, authorization) is appended to /tmp/record.jsonl.</summary>
    private static string StubNodeScript(int port) => $$"""
        const http = require('http');
        const fs = require('fs');
        const TOOL = JSON.stringify({path: {{StubJson(EditedFileName)}}, content: {{StubJson(EditedFileContent)}}});
        const toolCall = {index: 0, id: "call-stub-1", type: "function",
          function: {name: "write", arguments: TOOL} };
        const firstDelta = {role: "assistant", tool_calls: [toolCall] };
        const finalDelta = {role: "assistant", content: "done" };
        function chunk(delta, finish) {
          const payload = {id: "chatcmpl-stub", object: "chat.completion.chunk", created: 1,
            model: {{StubJson(StubModelId)}},
            choices: [{index: 0, delta: delta, finish_reason: finish} ] };
          return "data: " + JSON.stringify(payload) + "\n\n";
        }
        let turn = 0;
        http.createServer((req, res) => {
          let body = "";
          req.on("data", d => body += d);
          req.on("end", () => {
            let model = "";
            try { model = JSON.parse(body).model || ""; } catch (e) { model = ""; }
            fs.appendFileSync("/tmp/record.jsonl", JSON.stringify(
              {path: req.url, model: model, auth: req.headers.authorization || ""}) + "\n");
            res.writeHead(200, {"Content-Type": "text/event-stream"});
            if (turn === 0) {
              res.write(chunk(firstDelta, null));
              res.write(chunk({ }, "tool_calls"));
            } else {
              res.write(chunk(finalDelta, null));
              res.write(chunk({ }, "stop"));
            }
            res.end("data: [DONE]\n\n");
            turn++;
          });
        }).listen({{port}}, () => console.log("stub listening"));
        """;

    private static string StubJson(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    /// <summary>Runs the docker CLI directly (read-only stub orchestration; images are never
    /// pulled and the network is never modified). Throws on failure.</summary>
    private static (int ExitCode, string StdOut, string StdErr) RunDocker(
        IReadOnlyList<string> arguments, string? stdin, int timeout)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the docker CLI.");
        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
            throw new InvalidOperationException(
                "the docker CLI did not finish in time: docker " + string.Join(' ', arguments));
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} failed with exit {process.ExitCode}: " +
                stderr.Result.Trim());
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}

/// <summary>Opt-in gate for the Pi stub-endpoint category: discovered and skipped without
/// TENNINETY_RUN_DOCKER_PI_STUB_TESTS=1; once opted in, missing images or a missing network
/// FAIL the test body (a requested run is never converted into a skip).</summary>
public sealed class DockerPiStubFactAttribute : DockerCategoryFactAttribute
{
    public DockerPiStubFactAttribute()
        : base("TENNINETY_RUN_DOCKER_PI_STUB_TESTS",
            "TENNINETY_PI_TEST_IMAGE (the local pinned Pi coder image), " +
            "TENNINETY_REVIEWER_TEST_IMAGE + TENNINETY_TESTER_TEST_IMAGE (exact sha256:<64 hex> " +
            "local image IDs; the preflight probes every role) and TENNINETY_TEST_MODEL_NETWORK " +
            "(a pre-existing local network that hosts the test-run stub OpenAI server " +
            "container). Images are never pulled; no GPU or real model is needed.")
    {
    }
}
