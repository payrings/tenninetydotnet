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

public sealed class SandboxAgentGateTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly TempDir _managedRoot = new();
    private readonly GitService _git;
    private readonly TenNinetyConfig _config;
    private readonly string _mainSha;
    private readonly DaemonLockLease _lease;

    public SandboxAgentGateTests()
    {
        _git = new GitService(_repo.Root);
        _git.Init();
        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"), ".tenninety/\n");
        File.WriteAllText(Path.Combine(_repo.Root, "README.md"), "baseline\n");
        _mainSha = _git.CommitAll("initial")!;
        _git.CreateAndCheckoutBranch("work/WP-001");
        _lease = DaemonLock.Acquire(_repo.Root);
        _config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            CoderAgent = "aider",
            LocalModels = new LocalModelsConfig
            {
                Coder = "coder-model",
                Reviewer = "reviewer-model",
            },
            Sandbox = new SandboxConfig
            {
                WorkspaceRoot = _managedRoot.Root,
                Roles = new SandboxRolesConfig
                {
                    Coder = new CoderSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.CoderImageId,
                        ModelEndpoint = "http://coder-model:8000/v1",
                    },
                    Reviewer = new ReviewerSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.ReviewerImageId,
                    },
                    Tester = new TesterSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.TesterImageId,
                    },
                },
            },
        };
    }

    [Fact]
    public async Task Coder_removal_precedes_scan_and_only_the_validated_patch_is_promoted()
    {
        var timeline = new List<string>();
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        runtime.SessionFactory = spec => Session(spec, timeline, command =>
        {
            var path = Path.Combine(spec.HostWorkspacePath!.Value, "src", "change.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "validated\n");
        });
        var gate = CoderGate(runtime, timeline, out var transport);
        CandidateScanner.TraceHook = _ => timeline.Add("scan");
        try
        {
            var result = await gate.ImplementAsync(CoderContext());

            Assert.True(result.ProducedChanges);
            Assert.Equal(_git.HeadSha(), result.CommitSha);
            Assert.Equal("validated\n", File.ReadAllText(Path.Combine(_repo.Root, "src", "change.txt")));
            Assert.True(timeline.IndexOf("dispose") < timeline.IndexOf("scan"));
            Assert.True(timeline.IndexOf("scan") < timeline.IndexOf("delete"));
            Assert.Equal(SandboxRole.Coder, runtime.LastSpec!.Role);
            Assert.Equal(SandboxNetworkPolicy.Model, runtime.LastSpec.Network);
            Assert.Equal(_mainSha, runtime.LastSpec.CandidateSha);
            Assert.Equal("http://coder-model:8000/v1",
                runtime.LastSpec.Environment["OPENAI_BASE_URL"]);
            Assert.DoesNotContain(runtime.LastSpec.Environment.Keys,
                key => key.Contains("REVIEW", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("/usr/local/bin/aider",
                runtime.LastSession!.Commands.Single().Executable);
            Assert.True(transport.Disposed);
            Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
        }
        finally
        {
            CandidateScanner.TraceHook = null;
        }
    }

    [Fact]
    public async Task Policy_rejection_is_an_ordinary_no_change_result_after_cleanup()
    {
        var timeline = new List<string>();
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        runtime.SessionFactory = spec => Session(spec, timeline, _ =>
        {
            var path = Path.Combine(spec.HostWorkspacePath!.Value, "id_rsa");
            File.WriteAllText(path, "candidate controlled\n");
        });
        var gate = CoderGate(runtime, timeline, out var transport);

        var result = await gate.ImplementAsync(CoderContext());

        Assert.False(result.ProducedChanges);
        Assert.Equal(_mainSha, _git.HeadSha());
        Assert.Equal(["stop", "dispose", "delete", "transport-dispose"], timeline);
        Assert.True(transport.Disposed);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Coder_cancellation_checkpoints_only_after_removal_and_deletion()
    {
        using var cts = new CancellationTokenSource();
        var timeline = new List<string>();
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        runtime.SessionFactory = spec =>
        {
            var session = Session(spec, timeline, _ =>
            {
                File.WriteAllText(Path.Combine(spec.HostWorkspacePath!.Value, "partial.txt"), "safe\n");
                cts.Cancel();
            });
            session.ThrowOnCallerCancellation = false;
            session.Then(new SandboxCommandResult(
                -1, "", "", TimedOut: false, Cancelled: true,
                OomKilled: false, OutputTruncated: false, Duration: TimeSpan.Zero));
            return session;
        };
        var gate = CoderGate(runtime, timeline, out _);

        var ex = await Assert.ThrowsAsync<CoderCheckpointedCancellationException>(
            () => gate.ImplementAsync(CoderContext(), cts.Token));

        Assert.Equal(_git.HeadSha(), ex.CheckpointSha);
        Assert.True(timeline.IndexOf("dispose") < timeline.IndexOf("delete"));
        Assert.Equal("safe\n", File.ReadAllText(Path.Combine(_repo.Root, "partial.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Cancellation_before_materialization_cleans_owned_resources_without_promotion()
    {
        using var cts = new CancellationTokenSource();
        var fake = new PreflightFakeTransport(_config.Sandbox);
        var transport = new CancellingTransport(fake, cts);
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        var gate = new SandboxCoderGate(
            _git, _config, _lease, log: null,
            transportFactory: () => transport,
            runtimeFactory: (_, _) => runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, _config.Sandbox, root, _repo.Root),
            deleteWorkspaceOverride: DeleteWorkspace);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.ImplementAsync(CoderContext(), cts.Token));

        Assert.Null(runtime.LastSpec);
        Assert.True(transport.Disposed);
        Assert.Equal(_mainSha, _git.HeadSha());
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Reviewer_is_offline_uses_structured_actions_and_discards_guest_writes()
    {
        var timeline = new List<string>();
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        runtime.SessionFactory = spec => Session(spec, timeline, command =>
        {
            File.WriteAllText(Path.Combine(spec.HostWorkspacePath!.Value, "reviewer-write.txt"), "discard\n");
        });
        var chat = new QueueChat(
            "{\"action\":\"run\",\"command\":\"printf '; $(touch /tmp/nope)'\"}",
            "{\"action\":\"final\",\"verdict\":\"PASS\",\"reasons\":[]}");
        var gate = ReviewerGate(runtime, chat, timeline, out var transport);

        var result = await gate.ReviewAsync(ReviewerContext());

        Assert.True(result.Passed);
        Assert.Equal("reviewer-model", result.ReviewerModel);
        Assert.Equal(_mainSha, result.CandidateSha);
        Assert.Equal(SandboxRole.Reviewer, runtime.LastSpec!.Role);
        Assert.Equal(SandboxNetworkPolicy.None, runtime.LastSpec.Network);
        Assert.DoesNotContain(runtime.LastSpec.Environment.Keys,
            key => key.Contains("OPENAI", StringComparison.Ordinal));
        var command = runtime.LastSession!.Commands.Single();
        Assert.Equal("/bin/sh", command.Executable);
        Assert.Equal(["-lc", "printf '; $(touch /tmp/nope)'"], command.Arguments);
        Assert.Equal(_config.Sandbox.Roles.Reviewer.MaxActionOutputKb * 1024L,
            command.MaxOutputBytes);
        Assert.True(transport.Disposed);
        Assert.False(File.Exists(Path.Combine(_repo.Root, "reviewer-write.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Invalid_reviewer_protocol_runs_no_guest_action_and_reports_exact_model()
    {
        var timeline = new List<string>();
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        runtime.SessionFactory = spec => Session(spec, timeline, _ => { });
        var gate = ReviewerGate(runtime, new QueueChat("```json\n{}\n```"), timeline, out _);

        var result = await gate.ReviewAsync(ReviewerContext());

        Assert.False(result.Passed);
        Assert.Equal("reviewer-model", result.ReviewerModel);
        Assert.Empty(runtime.LastSession!.Commands);
        Assert.Equal(["stop", "dispose", "delete", "transport-dispose"], timeline);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    public void Dispose()
    {
        CandidateScanner.TraceHook = null;
        _lease.Dispose();
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private SandboxCoderGate CoderGate(
        SandboxTesterGateTests.RecordingRuntime runtime,
        List<string> timeline,
        out SandboxTesterGateTests.ForwardingTransport transport)
    {
        var fake = new PreflightFakeTransport(_config.Sandbox);
        transport = new SandboxTesterGateTests.ForwardingTransport(fake, timeline.Add);
        var ownedTransport = transport;
        return new SandboxCoderGate(
            _git, _config, _lease, log: null,
            transportFactory: () => ownedTransport,
            runtimeFactory: (_, _) => runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, _config.Sandbox, root, _repo.Root),
            deleteWorkspaceOverride: DeleteWorkspace(timeline));
    }

    private SandboxReviewerGate ReviewerGate(
        SandboxTesterGateTests.RecordingRuntime runtime,
        IChatClient chat,
        List<string> timeline,
        out SandboxTesterGateTests.ForwardingTransport transport)
    {
        var fake = new PreflightFakeTransport(_config.Sandbox);
        transport = new SandboxTesterGateTests.ForwardingTransport(fake, timeline.Add);
        var ownedTransport = transport;
        return new SandboxReviewerGate(
            _git, _config, chat, "reviewer-model", log: null,
            transportFactory: () => ownedTransport,
            runtimeFactory: (_, _) => runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, _config.Sandbox, root, _repo.Root),
            deleteWorkspaceOverride: DeleteWorkspace(timeline));
    }

    private RecordingSandboxSession Session(
        SandboxSpec spec, List<string> timeline, Action<SandboxCommand> onRun) => new()
    {
        Role = spec.Role,
        SourcePath = spec.HostWorkspacePath!.Value,
        EventSink = timeline.Add,
        OnRun = onRun,
    };

    private Task DeleteWorkspace(string path)
    {
        // The caller's timeline is supplied by the session/transport; record deletion by
        // locating the active list through the session sink before performing trusted deletion.
        SandboxTesterGate.DeleteAttemptDirectory(path, _managedRoot.Root);
        return Task.CompletedTask;
    }

    private Func<string, Task> DeleteWorkspace(List<string> timeline) => path =>
    {
        timeline.Add("delete");
        SandboxTesterGate.DeleteAttemptDirectory(path, _managedRoot.Root);
        return Task.CompletedTask;
    };

    private CoderRunContext CoderContext() => new()
    {
        Candidate = new CandidateRevision("work/WP-001", _mainSha, _mainSha),
        WorkPackage = WorkPackage(),
        Attempt = 1,
    };

    private ReviewerRunContext ReviewerContext() => new()
    {
        Candidate = new CandidateRevision("work/WP-001", _mainSha, _mainSha),
        WorkPackage = WorkPackage(),
        Attempt = 1,
    };

    private static WorkPackage WorkPackage() => new()
    {
        Id = "WP-001",
        Title = "Sandbox boundary",
        Goal = "Exercise the exact candidate",
        Directives = ["Remain isolated"],
        AcceptanceCriteria = ["Cleanup is proven"],
    };

    private sealed class QueueChat(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);
        public List<(string Model, string System, string User, long Limit)> Calls { get; } = [];

        public Task<string> CompleteAsync(
            string model, string system, string user, long maxResponseBytes, CancellationToken ct)
        {
            Calls.Add((model, system, user, maxResponseBytes));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class CancellingTransport(
        IDockerCliTransport inner, CancellationTokenSource cancellation)
        : IDockerCliTransport, IDisposable
    {
        private int _calls;
        public bool Disposed { get; private set; }

        public Task<DockerCliResult> RunAsync(
            DockerCliInvocation invocation, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) cancellation.Cancel();
            return inner.RunAsync(invocation, ct);
        }

        public void Dispose() => Disposed = true;
    }
}
