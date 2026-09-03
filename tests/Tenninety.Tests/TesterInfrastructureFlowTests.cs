using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Frontier;
using Tenninety.Git;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Real coordinator/engine control flow with a REAL SandboxTesterGate over fake Docker
/// dependencies: proves that gate-level infrastructure/refusal failures abort the run through
/// the engine's infrastructure-exception path (no second Coder attempt, no promotion, no
/// Frontier escalation), while ordinary candidate build/test failures keep the normal retry
/// and escalation path, and that caller cancellation propagates only after proven cleanup.
/// </summary>
public sealed class TesterInfrastructureFlowTests : IDisposable
{
    public TempDir Dir { get; } = new();
    public TempDir ManagedRoot { get; } = new();
    public GitService Git { get; }
    public TenNinetyConfig Config { get; }
    public RuntimeState State { get; } = new();
    public Plan Plan { get; }
    public SpyCoder Coder { get; }
    public StateStore States { get; }
    public AuditLog Audit { get; }
    public PreflightFakeTransport Transport { get; }
    public SandboxTesterGateTests.RecordingRuntime Runtime { get; } = new();
    public CountingFrontier Frontier { get; } = new();

    public TesterInfrastructureFlowTests()
    {
        Git = new GitService(Dir.Root);
        Git.Init();
        File.WriteAllText(System.IO.Path.Combine(Dir.Root, ".gitignore"), ".tenninety/\n");
        Directory.CreateDirectory(System.IO.Path.Combine(Dir.Root, ".tenninety"));
        File.WriteAllText(System.IO.Path.Combine(Dir.Root, "README.md"), "demo\n");
        File.WriteAllText(System.IO.Path.Combine(Dir.Root, "tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        Git.CommitAll("initial");
        // Docker-mode coders promote and return their own exact authoritative commit.
        Coder = new SpyCoder(Dir.Root, Git);

        Config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            MaxAttemptsBeforeEscalation = 2,
            MaxTotalAttempts = 3,
            BuildCommand = "dotnet build",
            TestCommand = "dotnet test",
            Mock = new MockBehaviorConfig(),
            Sandbox = new SandboxConfig
            {
                WorkspaceRoot = ManagedRoot.Root,
                Roles = new SandboxRolesConfig
                {
                    Coder = new CoderSandboxRoleConfig { Image = "sha256:" + new string('a', 64) },
                    Reviewer = new ReviewerSandboxRoleConfig { Image = "sha256:" + new string('b', 64) },
                    Tester = new TesterSandboxRoleConfig { Image = "sha256:" + new string('c', 64) },
                },
            },
        };
        States = new StateStore(System.IO.Path.Combine(Dir.Root, ".tenninety", "state.json"));
        Audit = new AuditLog(System.IO.Path.Combine(Dir.Root, ".tenninety", "audit-log.jsonl"));
        Plan = TestPlans.Simple();
        foreach (var wp in Plan.WorkPackages)
            State.QueueStatus[wp.Id] = wp.Status;
        Transport = new PreflightFakeTransport(Config.Sandbox);
    }

    public SandboxTesterGate MakeGate() =>
        new(Git, Config, log: null,
            transportFactory: () => new SandboxTesterGateTests.ForwardingTransport(Transport),
            runtimeFactory: (_, _) => Runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, Config.Sandbox, root, Dir.Root),
            deleteWorkspaceOverride: path =>
            {
                SandboxTesterGate.DeleteAttemptDirectory(path, Config.Sandbox.WorkspaceRoot!);
                return Task.CompletedTask;
            });

    public ExecutionEngine CreateEngine(ITesterAgent tester) =>
        new(Git, Config, Frontier, Coder, new ScriptedReviewer(0), tester, States, Audit);

    public void ScriptSuccessfulRun(Action<RecordingSandboxSession, string?>? configure = null)
    {
        Runtime.SessionFactory = spec =>
        {
            var s = new RecordingSandboxSession { SourcePath = spec.HostWorkspacePath?.Value };
            configure?.Invoke(s, s.SourcePath);
            return s;
        };
    }

    public void Dispose()
    {
        Dir.Dispose();
        ManagedRoot.Dispose();
    }

    // ---- infrastructure failures abort the run ------------------------------------------------

    [Fact]
    public async Task A_gate_infrastructure_failure_produces_no_second_coder_attempt_promotion_or_frontier_call()
    {
        Transport.MissingNetworks.Add(Config.Sandbox.ModelNetwork); // preflight refusal
        var engine = CreateEngine(MakeGate());

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => engine.ExecuteWpAsync(Plan.WorkPackages[0], State, CancellationToken.None));

        Assert.Contains("preflight", ex.Message);
        Assert.Equal(1, Coder.Contexts.Count); // exactly one attempt, no automatic retry
        Assert.Equal(0, Frontier.RepairAdviceCalls);
        Assert.Equal(0, Frontier.RevertCalls);
        Assert.DoesNotContain(Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.Contains(Audit.ReadTail(100), e =>
            e.Event == "TESTS_FAILED" && e.Detail.Contains("infrastructure exception"));
        // The run aborted with resumable state, never a promotion.
        Assert.Equal(TenNinety.WpStatus.Pending, Plan.WorkPackages[0].Status);
        Assert.Null(States.Load().CurrentWp);
        Assert.Equal("main", Git.CurrentBranch());
        Assert.True(Git.BranchExists("work/WP-001"));
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
    }

    // ---- ordinary test failures keep the retry path ---------------------------------------------

    [Fact]
    public async Task An_ordinary_test_failure_from_the_real_gate_keeps_the_normal_retry_path()
    {
        ScriptSuccessfulRun((s, _) =>
            s.Then(RecordingSandboxSession.Ok())          // build succeeds
             .Then(RecordingSandboxSession.Fail(1)));     // tests fail definitively
        var engine = CreateEngine(MakeGate());

        var outcome = await engine.ExecuteWpAsync(Plan.WorkPackages[0], State, CancellationToken.None);

        Assert.Equal(WpOutcome.Blocked, outcome); // 3 total attempts exhausted
        Assert.Equal(3, Coder.Contexts.Count);    // the failure was retried
        Assert.Equal(1, Frontier.RepairAdviceCalls); // ordinary failures may escalate
        Assert.Contains(Audit.ReadTail(100), e => e.Event == "TESTS_FAILED");
        Assert.DoesNotContain(Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.Equal(TenNinety.WpStatus.Blocked, Plan.WorkPackages[0].Status);
        Assert.Equal("main", Git.CurrentBranch());
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root)); // cleanup proven every attempt
    }

    [Fact]
    public async Task An_ordinary_test_failure_promotes_once_the_suite_passes()
    {
        var attempts = 0;
        ScriptSuccessfulRun((s, _) =>
        {
            if (++attempts == 1)
                s.Then(RecordingSandboxSession.Ok()).Then(RecordingSandboxSession.Fail(1));
            // second attempt falls through to the default clean pass
        });
        var engine = CreateEngine(MakeGate());

        var outcome = await engine.ExecuteWpAsync(Plan.WorkPackages[0], State, CancellationToken.None);

        Assert.Equal(WpOutcome.Done, outcome);
        Assert.Equal(2, Coder.Contexts.Count);
        Assert.Contains(Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
    }

    // ---- cancellation through the real engine ---------------------------------------------------

    [Fact]
    public async Task Cancellation_during_the_test_command_propagates_after_cleanup_through_the_engine()
    {
        using var cts = new CancellationTokenSource();
        var submissions = 0;
        ScriptSuccessfulRun((s, _) =>
        {
            s.ThrowOnCallerCancellation = false;
            s.OnRun = _ =>
            {
                submissions++;
                if (submissions == 2) cts.Cancel(); // cancel while the SECOND command runs
            };
            s.Then(RecordingSandboxSession.Ok("build succeeded")); // FIRST command: build
            s.Then(new SandboxCommandResult(                       // SECOND command: test
                0, "partial", "", TimedOut: false, Cancelled: true,
                OomKilled: false, OutputTruncated: false, Duration: TimeSpan.FromSeconds(1)));
        });
        var engine = CreateEngine(MakeGate());

        var outcome = await engine.ExecuteWpAsync(Plan.WorkPackages[0], State, cts.Token);

        // The engine treats the propagated cancellation as an interruption with checkpointed,
        // resumable state — and the gate's cleanup still proved the workspace deletion BEFORE
        // the interrupted outcome surfaced.
        Assert.Equal(WpOutcome.Stopped, outcome);
        Assert.Equal(2, submissions);
        // Both exact submissions happened: build first, then the test command.
        var commands = Runtime.LastSession!.Commands;
        Assert.Equal(2, commands.Count);
        Assert.Contains("dotnet build", commands[0].Arguments[^1]);
        Assert.Contains("dotnet test", commands[1].Arguments[^1]);
        Assert.Equal("main", Git.CurrentBranch());
        Assert.True(Git.IsClean());
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
        Assert.DoesNotContain(Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
    }

    [Fact]
    public async Task A_synthetic_infrastructure_failure_during_tests_aborts_without_retry_escalation_or_promotion()
    {
        // The transport represented a startup/I/O failure as a synthetic negative exit with
        // NO operational flag. The Tester boundary classifies it as an infrastructure
        // failure: the engine aborts — no second Coder attempt, no Frontier escalation and
        // no promotion — while cleanup is still proven.
        ScriptSuccessfulRun((s, _) =>
            s.Then(RecordingSandboxSession.Ok("build ok"))     // build succeeds
             .Then(new SandboxCommandResult(                   // synthetic failure, no flags
                 -1, "docker process failed to start: IOException", "",
                 TimedOut: false, Cancelled: false,
                 OomKilled: false, OutputTruncated: false,
                 Duration: TimeSpan.FromMilliseconds(1))));
        var engine = CreateEngine(MakeGate());

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => engine.ExecuteWpAsync(Plan.WorkPackages[0], State, CancellationToken.None));

        Assert.Contains("could not produce a definitive exit code", ex.Message);
        Assert.Equal(1, Coder.Contexts.Count);   // exactly one attempt, no automatic retry
        Assert.Equal(0, Frontier.RepairAdviceCalls);
        Assert.Equal(0, Frontier.RevertCalls);
        Assert.DoesNotContain(Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.Contains(Audit.ReadTail(100), e =>
            e.Event == "TESTS_FAILED" && e.Detail.Contains("infrastructure exception"));
        Assert.Equal(TenNinety.WpStatus.Pending, Plan.WorkPackages[0].Status);
        Assert.Equal("main", Git.CurrentBranch());
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root)); // cleanup proven
    }

    [Fact]
    public async Task An_ordinary_definitive_nonzero_exit_still_retries_after_proven_cleanup()
    {
        // The synthetic-failure repair must not swallow ordinary definitive nonzero exits:
        // they remain ordinary candidate failures with cleanup proven and the retry path
        // (including escalation) intact.
        ScriptSuccessfulRun((s, _) =>
            s.Then(RecordingSandboxSession.Ok())          // build succeeds
             .Then(RecordingSandboxSession.Fail(7)));     // definitive nonzero test exit
        var engine = CreateEngine(MakeGate());

        var outcome = await engine.ExecuteWpAsync(Plan.WorkPackages[0], State, CancellationToken.None);

        Assert.Equal(WpOutcome.Blocked, outcome);  // 3 total attempts exhausted
        Assert.Equal(3, Coder.Contexts.Count);     // the failure was retried
        Assert.Equal(1, Frontier.RepairAdviceCalls);
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root)); // cleanup proven each attempt
    }

    // ---- a stateful counting frontier -------------------------------------------------------------

    public sealed class CountingFrontier : IFrontierClient
    {
        public int RepairAdviceCalls { get; private set; }
        public int RevertCalls { get; private set; }

        public Task<Plan> GeneratePlanAsync(string sanitizedSpecMarkdown, CancellationToken ct = default) =>
            Task.FromResult(new Plan { ProjectName = "unused" });

        public Task<RepairAdvice> GetRepairAdviceAsync(RepairRequest request, CancellationToken ct = default)
        {
            RepairAdviceCalls++;
            return Task.FromResult(new RepairAdvice
            {
                Analysis = "counting frontier",
                Advice = ["keep going"],
            });
        }

        public Task<PivotProposal> ProposePivotAsync(PivotRequest request, CancellationToken ct = default) =>
            Task.FromResult(new PivotProposal());

        public Task<RevertGuidance> ProposeRevertAsync(RevertRequest request, CancellationToken ct = default)
        {
            RevertCalls++;
            return Task.FromResult(new RevertGuidance
            {
                Analysis = "mechanical revert",
                MechanicalRevertSufficient = true,
                Steps = ["revert"],
            });
        }
    }
}
