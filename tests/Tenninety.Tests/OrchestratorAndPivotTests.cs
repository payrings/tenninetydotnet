using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Tests;

public class OrchestratorSelectionTests
{
    [Fact]
    public void Selects_lowest_id_ready_work_package()
    {
        var plan = TestPlans.Simple(); // 001 → 002 → 003
        var state = new RuntimeState();
        var o = MakeOrchestrator(plan, state);

        Assert.Equal("WP-001", o.SelectNextReady()!.Id);
        plan.WorkPackages[0].Status = TenNinety.WpStatus.Done;
        Assert.Equal("WP-002", o.SelectNextReady()!.Id);
        plan.WorkPackages[1].Status = TenNinety.WpStatus.Done;
        Assert.Equal("WP-003", o.SelectNextReady()!.Id);
        plan.WorkPackages[2].Status = TenNinety.WpStatus.Done;
        Assert.Null(o.SelectNextReady());
        Assert.True(o.AllTerminal());
    }

    [Fact]
    public void Blocked_dependency_makes_dependents_unready()
    {
        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        var o = MakeOrchestrator(plan, state);

        plan.WorkPackages[0].Status = TenNinety.WpStatus.Blocked;
        Assert.Null(o.SelectNextReady());
        Assert.False(o.AllTerminal());
    }

    [Fact]
    public void Cancelled_wps_are_terminal()
    {
        var plan = TestPlans.Simple();
        foreach (var wp in plan.WorkPackages)
            wp.Status = wp.Id == "WP-003" ? TenNinety.WpStatus.Cancelled : TenNinety.WpStatus.Done;
        var o = MakeOrchestrator(plan, new RuntimeState());
        Assert.True(o.AllTerminal());
    }

    [Fact]
    public void Conflict_wps_are_never_scheduled()
    {
        // Blueprint v3.2 Enterprise: CONFLICT packages carry no directives and await human resolution.
        var plan = TestPlans.Simple();
        plan.WorkPackages[0].Notes = "CONFLICT: spec contradicts itself on task ownership.";
        var o = MakeOrchestrator(plan, new RuntimeState());

        Assert.Null(o.SelectNextReady());       // ready by dependencies, yet never selected
        Assert.False(o.AllTerminal());
    }

    [Fact]
    public void Pivot_rework_can_clear_conflict_marker_and_resume_scheduling()
    {
        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        foreach (var wp in plan.WorkPackages)
            state.QueueStatus[wp.Id] = wp.Status;
        var conflicted = plan.WorkPackages[0];
        conflicted.Notes = "CONFLICT: contradictory ownership rule.";
        conflicted.Directives.Clear();

        // Human resolves the conflict through a pivot REWORK with fresh directives.
        PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-002", "WP-003" },
            Rework =
            {
                new PivotRework
                {
                    Id = "WP-001",
                    Reason = "conflict resolved in spec v2",
                    UpdatedDirectives = { "implement the resolved rule" },
                },
            },
        }, plan, state);

        var o = MakeOrchestrator(plan, state);
        Assert.Equal("WP-001", o.SelectNextReady()!.Id);
    }

    private static Orchestrator MakeOrchestrator(Plan plan, RuntimeState state)
    {
        using var dir = new TempDir();
        // Orchestrator only needs the collaborators for construction; selection logic is pure.
        return new Orchestrator(
            new Tenninety.Git.GitService(dir.Root), plan, state,
            new TenNinetyConfig(), new MockFrontierClient(),
            new StateStore(dir.Path("state.json")), new AuditLog(dir.Path("audit.jsonl")));
    }
}

public class OrchestratorRunTests
{
    [Fact]
    public async Task A_blocked_leaf_returns_deadlock_instead_of_success()
    {
        using var tmp = new TempDir();
        var git = new Tenninety.Git.GitService(tmp.Root);
        git.Init();
        Directory.CreateDirectory(tmp.Path(".tenninety"));
        File.WriteAllText(tmp.Path(".tenninety/.gitignore"), RuntimeGitignoreMigration.Contents);
        File.WriteAllText(tmp.Path("README.md"), "demo");
        git.CommitPaths([".tenninety/.gitignore", "README.md"], "initial");

        var plan = new Plan
        {
            ProjectName = "Blocked leaf",
            WorkPackages = { TestPlans.Wp("WP-001", "INFRA") },
        };
        var state = new RuntimeState { QueueStatus = { ["WP-001"] = TenNinety.WpStatus.Pending } };
        var config = new TenNinetyConfig
        {
            MaxAttemptsBeforeEscalation = 1,
            MaxTotalAttempts = 1,
            Mock = new MockBehaviorConfig
            {
                ReviewerFailAttempts = 100,
                ReviewerIgnoresAdvice = true,
            },
        };
        var audit = new AuditLog(tmp.Path(".tenninety/audit-log.jsonl"));
        var orchestrator = new Orchestrator(
            git, plan, state, config, new MockFrontierClient(),
            new StateStore(tmp.Path(".tenninety/state.json")), audit);

        var outcome = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(OrchestratorExit.Deadlocked, outcome);
        Assert.Equal(TenNinety.WpStatus.Blocked, plan.WorkPackages[0].Status);
        Assert.Contains(audit.ReadTail(20), e =>
            e.Event == "QUEUE_DEADLOCKED" && e.Detail.Contains("WP-001"));
    }
}

public class PivotServiceTests
{
    private static (Plan Plan, RuntimeState State, AuditLog Audit) Setup()
    {
        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        foreach (var wp in plan.WorkPackages)
        {
            state.QueueStatus[wp.Id] = wp.Status;
            if (wp.Id == "WP-001")
                state.Attempts["WP-001"] = new AttemptInfo { Count = 7, Total = 7 };
        }
        return (plan, state, new AuditLog(System.IO.Path.Combine(
            Directory.CreateTempSubdirectory("pivot").FullName, "audit.jsonl")));
    }

    [Fact]
    public void Rework_resets_status_and_attempts()
    {
        var (plan, state, audit) = Setup();
        plan.WorkPackages[0].Status = TenNinety.WpStatus.Done;

        var result = PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-002", "WP-003" },
            Rework =
            {
                new PivotRework
                {
                    Id = "WP-001",
                    Reason = "spec changed",
                    UpdatedDirectives = { "new directive" },
                },
            },
        }, plan, state);

        var wp001 = plan.WorkPackages.Single(w => w.Id == "WP-001");
        Assert.Equal(TenNinety.WpStatus.Pending, wp001.Status);
        Assert.Equal(new List<string> { "new directive" }, wp001.Directives);
        Assert.False(state.Attempts.ContainsKey("WP-001")); // budget reset
        Assert.Empty(result.Cancelled);
        Assert.Equal(2, result.Kept); // keep count
        Assert.Equal(TenNinety.WpStatus.Pending, state.QueueStatus["WP-001"]);
    }

    [Fact]
    public void Cancel_marks_cancelled()
    {
        var (plan, state, audit) = Setup();
        PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-001", "WP-002" },
            Cancel = { new PivotCancel { Id = "WP-003", Reason = "not needed" } },
        }, plan, state);

        Assert.Equal(TenNinety.WpStatus.Cancelled, plan.WorkPackages.Single(w => w.Id == "WP-003").Status);
        Assert.Equal(TenNinety.WpStatus.Cancelled, state.QueueStatus["WP-003"]);
    }

    [Fact]
    public void New_work_packages_are_added_pending_and_validated()
    {
        var (plan, state, audit) = Setup();
        var fresh = TestPlans.Wp("WP-900", layer: "API", deps: "WP-003");

        var result = PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-001", "WP-002", "WP-003" },
            NewWorkPackages = { fresh },
        }, plan, state);

        Assert.Contains("WP-900", result.Added);
        Assert.Equal(TenNinety.WpStatus.Pending, plan.WorkPackages.Single(w => w.Id == "WP-900").Status);
    }

    [Fact]
    public void Pivot_that_breaks_the_dag_is_rejected()
    {
        var (plan, state, audit) = Setup();
        var bad = TestPlans.Wp("WP-950", deps: "WP-999"); // unknown dependency
        var planBefore = Json.Serialize(plan);
        var stateBefore = Json.Serialize(state);

        Assert.Throws<InvalidOperationException>(() => PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-001", "WP-002", "WP-003" },
            NewWorkPackages = { bad },
        }, plan, state));
        Assert.Equal(planBefore, Json.Serialize(plan));
        Assert.Equal(stateBefore, Json.Serialize(state));
    }

    [Fact]
    public void Unknown_ids_are_rejected()
    {
        var (plan, state, audit) = Setup();
        Assert.Throws<InvalidOperationException>(() => PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-404" },
        }, plan, state));
    }
}

public class PivotHardeningTests
{
    private static (Plan Plan, RuntimeState State, AuditLog Audit) Setup()
    {
        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        foreach (var wp in plan.WorkPackages)
            state.QueueStatus[wp.Id] = wp.Status;
        return (plan, state, new AuditLog(System.IO.Path.Combine(
            Directory.CreateTempSubdirectory("pivot-hardening").FullName, "audit.jsonl")));
    }

    [Fact]
    public void Unclassified_packages_are_rejected()
    {
        var (plan, state, audit) = Setup();
        plan.WorkPackages.Single(w => w.Id == "WP-003").Status = TenNinety.WpStatus.Pending;

        var ex = Assert.Throws<InvalidOperationException>(() => PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-001" },          // WP-002 missing entirely
            Rework = { },
        }, plan, state));

        Assert.Contains("unclassified", ex.Message);
        Assert.Contains("WP-002", ex.Message);
        Assert.Contains("WP-003", ex.Message);
    }

    [Fact]
    public void A_package_in_two_buckets_is_rejected()
    {
        var (plan, state, audit) = Setup();
        Assert.Throws<InvalidOperationException>(() => PivotService.Apply(new PivotProposal
        {
            Keep = { "WP-001", "WP-001" },
            Rework = { new PivotRework { Id = "WP-001", Reason = "also rework" } },
        }, plan, state));
    }

    [Fact]
    public void Cancelling_a_dependency_cascades_to_its_dependents()
    {
        var (plan, state, audit) = Setup(); // 001 → 002 → 003

        var result = PivotService.Apply(new PivotProposal
        {
            Keep = { },
            Cancel = { new PivotCancel { Id = "WP-001", Reason = "foundation dropped" } },
        }, plan, state);

        Assert.Equal(new[] { "WP-002", "WP-003" }, result.CancelledByCascade);
        Assert.All(plan.WorkPackages, w => Assert.Equal(TenNinety.WpStatus.Cancelled, w.Status));
        Assert.True(plan.WorkPackages.All(w => w.IsTerminal)); // no silent deadlock possible
    }

    [Fact]
    public void New_package_cannot_depend_on_cancelled_work()
    {
        var (plan, state, audit) = Setup();
        var newWp = TestPlans.Wp("WP-900", "API", "WP-001");

        var ex = Assert.Throws<InvalidOperationException>(() => PivotService.Apply(new PivotProposal
        {
            Cancel = { new PivotCancel { Id = "WP-001", Reason = "removed" } },
            NewWorkPackages = { newWp },
        }, plan, state));

        Assert.Contains("depend on cancelled work", ex.Message);
        Assert.DoesNotContain(plan.WorkPackages, w => w.Id == "WP-900");
    }
}

public class RevertServiceTests
{
    [Fact]
    public async Task Revert_refuses_to_start_from_a_feature_branch()
    {
        using var tmp = new TempDir();
        var git = new Tenninety.Git.GitService(tmp.Root);
        git.Init();
        File.WriteAllText(tmp.Path("README.md"), "initial");
        git.CommitAll("initial");
        var target = git.HeadSha();
        git.CreateAndCheckoutBranch("feature/unrelated");
        File.WriteAllText(tmp.Path("feature.txt"), "unrelated");
        git.CommitAll("unrelated feature");

        var service = new RevertService(
            git, new TenNinetyConfig(), new MockFrontierClient(), new ScriptedTester(0),
            new AuditLog(tmp.Path("audit.jsonl")));

        var outcome = await service.RevertAsync(target, "test");

        Assert.False(outcome.Success);
        Assert.Contains("must start from 'main'", outcome.Message);
        Assert.Equal("feature/unrelated", git.CurrentBranch());
        Assert.Equal(target, git.FindCommit("main")!.Sha);
    }

    [Fact]
    public async Task Revert_refuses_test_commits_after_the_mechanical_change()
    {
        using var tmp = new TempDir();
        var git = new Tenninety.Git.GitService(tmp.Root);
        git.Init();
        File.WriteAllText(tmp.Path(".gitignore"), ".tenninety/\n");
        File.WriteAllText(tmp.Path("README.md"), "initial");
        git.CommitAll("initial");
        File.WriteAllText(tmp.Path("feature.txt"), "promoted change");
        git.CommitAll("WP-001: feature [work package]");
        var target = git.HeadSha();

        var service = new RevertService(
            git, new TenNinetyConfig(), new MockFrontierClient(), new CommittingPassingTester(git),
            new AuditLog(tmp.Path("audit.jsonl")));

        var outcome = await service.RevertAsync(target, "bad promotion");

        Assert.False(outcome.Success);
        Assert.Contains("test command changed", outcome.Message);
        Assert.Equal(target, git.FindCommit("main")!.Sha);
        Assert.NotNull(outcome.BranchLeftBehind);
        Assert.True(git.BranchExists(outcome.BranchLeftBehind!));
    }

    private sealed class CommittingPassingTester(Tenninety.Git.IGitService git) : ITesterAgent
    {
        public Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
        {
            File.WriteAllText(System.IO.Path.Combine(git.RepoPath, "test-mutation.txt"), "unreviewed");
            git.CommitAll("test mutation");
            return Task.FromResult(new TestRunResult
            {
                Passed = true,
                ExitCode = 0,
                CandidateSha = ctx.Candidate.CommitSha,
            });
        }
    }

    // ---- Phase 5A: exact post-revert candidate identity ---------------------------------

    private sealed class IdentityRecordingTester(
        Tenninety.Git.IGitService git,
        List<string> recorded,
        string? forcedSha) : ITesterAgent
    {
        public Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
        {
            recorded.Add(ctx.Candidate.CommitSha);
            return Task.FromResult(new TestRunResult
            {
                Passed = true,
                ExitCode = 0,
                Command = "(identity-recording)",
                // forcedSha "" → no identity at all; otherwise the forced value.
                CandidateSha = forcedSha is null ? ctx.Candidate.CommitSha
                    : forcedSha.Length == 0 ? null
                    : forcedSha,
            });
        }
    }

    private static (RevertService Service, GitService Git, TempDir Dir) MakeRevertFixture(
        Func<GitService, ITesterAgent> testerFactory)
    {
        var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();
        File.WriteAllText(tmp.Path(".gitignore"), ".tenninety/\n");
        Directory.CreateDirectory(tmp.Path(".tenninety"));
        File.WriteAllText(tmp.Path("README.md"), "initial");
        git.CommitAll("initial");
        File.WriteAllText(tmp.Path("feature.txt"), "promoted change");
        git.CommitAll("WP-001: feature [work package]");
        return (new RevertService(
            git, new TenNinetyConfig(), new MockFrontierClient(), testerFactory(git),
            new AuditLog(System.IO.Path.Combine(tmp.Root, ".tenninety", "audit.jsonl"))), git, tmp);
    }

    [Fact]
    public async Task Revert_testing_binds_to_the_exact_post_revert_hotfix_sha()
    {
        var recorded = new List<string>();
        var (service, git, dir) = MakeRevertFixture(g =>
            new IdentityRecordingTester(g, recorded, forcedSha: null));
        using (dir)
        {
            var target = git.FindCommit("main")!.Sha; // the WP-001 promotion

            var outcome = await service.RevertAsync(target, "bad promotion");

            Assert.True(outcome.Success, outcome.Message);
            var sha = Assert.Single(recorded);
            // The tested candidate is the mechanical revert commit itself: NOT the reverted
            // promotion and NOT current main (the squash merge creates a new commit).
            Assert.NotEqual(target, sha);
            Assert.NotEqual(git.FindCommit(TenNinety.MainBranch)!.Sha, sha);
            Assert.True(TesterRunContext.IsFullCommitSha(sha));
            // The tested revert content is exactly what was promoted to main.
            Assert.Equal(
                git.ResolveTreeOfCommit(git.FindCommit(TenNinety.MainBranch)!.Sha),
                git.ResolveTreeOfCommit(sha));
        }
    }

    [Fact]
    public async Task Revert_refuses_a_pass_bound_to_a_wrong_or_missing_candidate_sha()
    {
        foreach (var forced in new[] { new string('f', 40), "" })
        {
            var recorded = new List<string>();
            var (service, git, dir) = MakeRevertFixture(g =>
                new IdentityRecordingTester(g, recorded, forced));
            using (dir)
            {
                var mainBefore = git.FindCommit(TenNinety.MainBranch)!.Sha;
                var target = git.FindCommit("main")!.Sha;

                var outcome = await service.RevertAsync(target, "bad promotion");

                Assert.False(outcome.Success);
                Assert.Contains("candidate", outcome.Message);
                Assert.NotNull(outcome.BranchLeftBehind); // refusal keeps the branch
                Assert.Equal(mainBefore, git.FindCommit(TenNinety.MainBranch)!.Sha);
                Assert.Equal("main", git.CurrentBranch());
                Assert.Single(recorded); // exactly one tester invocation per attempt
            }
        }
    }

    [Fact]
    public async Task Hotfix_validation_uses_the_sandbox_coordinator_selected_for_normal_runs()
    {
        // The same factory/selection governs the engine and the revert flow; in docker mode
        // both use the offline SandboxTesterGate (no separate revert-only host path exists).
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        var config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            Sandbox = new SandboxConfig
            {
                Roles = new SandboxRolesConfig
                {
                    Coder = new CoderSandboxRoleConfig { Image = "sha256:" + new string('a', 64) },
                    Reviewer = new ReviewerSandboxRoleConfig { Image = "sha256:" + new string('b', 64) },
                    Tester = new TesterSandboxRoleConfig { Image = "sha256:" + new string('c', 64) },
                },
            },
        };
        var tester = new AgentFactory(config).CreateTester(git);
        var service = new RevertService(
            git, config, new MockFrontierClient(), tester,
            new AuditLog(tmp.Path("audit.jsonl")));

        Assert.IsType<SandboxTesterGate>(tester);
        Assert.NotNull(service);
    }

    // ---- Phase 5A repair: the hotfix flow runs the REAL gate over fake Docker ------------------

    private static (RevertService Service, GitService Git, TempDir Dir, TempDir ManagedRoot,
        SandboxTesterGateTests.RecordingRuntime Runtime, PreflightFakeTransport Transport)
        MakeHotfixSandboxFixture()
    {
        var dir = new TempDir();
        var managedRoot = new TempDir();
        var git = new GitService(dir.Root);
        git.Init();
        File.WriteAllText(dir.Path(".gitignore"), ".tenninety/\n");
        Directory.CreateDirectory(dir.Path(".tenninety"));
        File.WriteAllText(dir.Path("README.md"), "initial");
        File.WriteAllText(dir.Path("tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        git.CommitAll("initial");
        File.WriteAllText(dir.Path("feature.txt"), "promoted change");
        git.CommitAll("WP-001: feature [work package]");

        var config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            BuildCommand = "dotnet build",
            TestCommand = "dotnet test",
            Sandbox = new SandboxConfig
            {
                WorkspaceRoot = managedRoot.Root,
                Roles = new SandboxRolesConfig
                {
                    Coder = new CoderSandboxRoleConfig { Image = "sha256:" + new string('a', 64) },
                    Reviewer = new ReviewerSandboxRoleConfig { Image = "sha256:" + new string('b', 64) },
                    Tester = new TesterSandboxRoleConfig { Image = "sha256:" + new string('c', 64) },
                },
            },
        };
        var transport = new PreflightFakeTransport(config.Sandbox);
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        var gate = new SandboxTesterGate(git, config, log: null,
            transportFactory: () => new SandboxTesterGateTests.ForwardingTransport(transport),
            runtimeFactory: (_, _) => runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(cli, config.Sandbox, root, dir.Root),
            deleteWorkspaceOverride: path =>
            {
                SandboxTesterGate.DeleteAttemptDirectory(path, config.Sandbox.WorkspaceRoot!);
                return Task.CompletedTask;
            });
        var service = new RevertService(git, config, new MockFrontierClient(), gate,
            new AuditLog(dir.Path(".tenninety/audit-hotfix.jsonl")));
        return (service, git, dir, managedRoot, runtime, transport);
    }

    [Fact]
    public async Task A_hotfix_revert_runs_through_the_real_sandbox_gate_and_promotes_the_exact_candidate()
    {
        var (service, git, dir, managedRoot, runtime, transport) = MakeHotfixSandboxFixture();
        using (dir)
        using (managedRoot)
        {
            var target = git.FindCommit(TenNinety.MainBranch)!.Sha; // the WP-001 promotion

            // Real temporary-Git revert; the Tester candidate is tested inside the offline
            // sandbox workspace through the REAL gate (fake Docker runtime/session/transport).
            var outcome = await service.RevertAsync(target, "bad promotion");

            Assert.True(outcome.Success, outcome.Message);
            Assert.True(transport.CreatedProbes > 0, "the real preflight ran against Docker");
            var spec = Assert.IsType<SandboxSpec>(runtime.LastSpec);
            // The tested candidate is the EXACT post-revert hotfix commit, recorded before
            // any test ran: not the reverted promotion and not the new main tip.
            Assert.True(TesterRunContext.IsFullCommitSha(spec.CandidateSha));
            Assert.NotEqual(target, spec.CandidateSha);
            Assert.NotEqual(git.FindCommit(TenNinety.MainBranch)!.Sha, spec.CandidateSha);
            var hotfixCommit = git.FindCommit(spec.CandidateSha);
            Assert.NotNull(hotfixCommit);
            Assert.StartsWith("Revert", hotfixCommit!.Subject);
            // The promoted main content equals the tested revert content.
            Assert.Equal(
                git.ResolveTreeOfCommit(git.FindCommit(TenNinety.MainBranch)!.Sha),
                git.ResolveTreeOfCommit(spec.CandidateSha));
            // Offline workspace execution: no network, a disposable workspace path, never
            // the authoritative checkout.
            Assert.Equal(SandboxNetworkPolicy.None, spec.Network);
            Assert.Equal("tester", spec.Labels["tenninety.role"]);
            Assert.StartsWith(managedRoot.Root, spec.HostWorkspacePath!.Value);
            Assert.NotEqual(dir.Root, spec.HostWorkspacePath!.Value);
            Assert.EndsWith("/source", spec.HostWorkspacePath!.Value);
            Assert.Equal(SandboxRole.Tester, spec.Role);
            // Cleanup: every attempt workspace was removed with the disposable .git.
            Assert.Empty(Directory.GetFileSystemEntries(managedRoot.Root));
            Assert.Equal("main", git.CurrentBranch());
            Assert.True(git.IsClean());
        }
    }

    [Fact]
    public async Task A_hotfix_revert_refuses_promotion_after_a_gate_infrastructure_failure()
    {
        var (service, git, dir, managedRoot, runtime, _) = MakeHotfixSandboxFixture();
        using (dir)
        using (managedRoot)
        {
            var mainBefore = git.FindCommit(TenNinety.MainBranch)!.Sha;
            var target = git.FindCommit(TenNinety.MainBranch)!.Sha;
            runtime.SessionFactory = _ =>
                throw new InvalidOperationException("container creation could not be proven");

            var outcome = await service.RevertAsync(target, "bad promotion");

            // The infrastructure failure is not converted into a promotion or a retry.
            Assert.False(outcome.Success);
            Assert.NotNull(outcome.BranchLeftBehind);
            Assert.True(git.BranchExists(outcome.BranchLeftBehind!));
            Assert.Equal(mainBefore, git.FindCommit(TenNinety.MainBranch)!.Sha);
            Assert.Equal("main", git.CurrentBranch());
            Assert.DoesNotContain(AuditEventsFor(dir), e => e.Event == "REVERT_PROMOTED");
        }
    }

    private static IReadOnlyList<AuditEvent> AuditEventsFor(TempDir dir) =>
        new AuditLog(dir.Path(".tenninety/audit-hotfix.jsonl")).ReadTail(100);
}
