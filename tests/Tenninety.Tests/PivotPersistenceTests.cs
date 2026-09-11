using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Cli;
using Tenninety.Execution;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Tests;

public sealed class PivotPersistenceTests
{
    [Fact]
    public void Approved_pivot_refuses_a_stale_in_memory_state_without_creating_an_intent()
    {
        using var fixture = new PivotFixture();
        var plan = fixture.Plans.Load();
        var staleState = fixture.States.Load();
        fixture.States.Update(current => current.Paused = true);
        var planBefore = File.ReadAllBytes(fixture.Plans.Path);
        using var lease = DaemonLock.Acquire(fixture.Root);

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Persistence().ApplyApproved(ReworkFirst(), plan, staleState, lease));

        Assert.Contains("progress changed", error.Message);
        Assert.False(fixture.Persistence().HasPending);
        Assert.Equal(planBefore, File.ReadAllBytes(fixture.Plans.Path));
        Assert.True(fixture.States.Load().Paused);
    }

    [Fact]
    public void State_save_failure_retains_recoverable_rework_without_mutating_tui_objects()
    {
        using var fixture = new PivotFixture();
        var plan = fixture.Plans.Load();
        var state = fixture.States.Load();
        var persistence = fixture.Persistence();
        fixture.States.BeforeSave = saved =>
        {
            if (saved.QueueStatus["WP-001"] == TenNinety.WpStatus.Pending)
                throw new IOException("simulated state replacement failure");
        };

        using var lease = DaemonLock.Acquire(fixture.Root);
        var error = Assert.Throws<InvalidOperationException>(() =>
            persistence.ApplyApproved(ReworkFirst(), plan, state, lease));

        Assert.Contains(TenNinety.PivotFile, error.Message);
        Assert.True(persistence.HasPending);
        Assert.Equal(TenNinety.WpStatus.Pending,
            fixture.Plans.Load().WorkPackages[0].Status);
        var oldState = fixture.States.Load();
        Assert.Equal(TenNinety.WpStatus.Done, oldState.QueueStatus["WP-001"]);
        Assert.True(oldState.Attempts.ContainsKey("WP-001"));
        Assert.Equal(TenNinety.WpStatus.Done, plan.WorkPackages[0].Status);
        Assert.True(state.Attempts.ContainsKey("WP-001"));

        fixture.States.BeforeSave = null;
        Assert.True(persistence.RecoverPending(lease));
        Assert.False(persistence.RecoverPending(lease));

        var recoveredPlan = fixture.Plans.Load();
        var recoveredState = fixture.States.Load();
        Assert.Equal(["reworked directive"], recoveredPlan.WorkPackages[0].Directives);
        Assert.Equal(TenNinety.WpStatus.Pending,
            recoveredState.QueueStatus["WP-001"]);
        Assert.False(recoveredState.Attempts.ContainsKey("WP-001"));
        Assert.False(persistence.HasPending);
        Assert.Equal(PivotPersistence.CommitMessage,
            fixture.Git.RecentCommits(1).Single().Subject);
    }

    [Theory]
    [InlineData((int)PivotPersistencePhase.BeforeCommit)]
    [InlineData((int)PivotPersistencePhase.AfterCommit)]
    public void Commit_boundary_recovery_is_controlled_repeatable_and_preserves_unrelated_edits(
        int phaseValue)
    {
        var phase = (PivotPersistencePhase)phaseValue;
        using var fixture = new PivotFixture();
        var persistence = fixture.Persistence();
        persistence.Failpoint = reached =>
        {
            if (reached == phase) throw new IOException($"interrupted at {phase}");
        };
        var plan = fixture.Plans.Load();
        var state = fixture.States.Load();
        using var lease = DaemonLock.Acquire(fixture.Root);

        Assert.Throws<InvalidOperationException>(() =>
            persistence.ApplyApproved(ReworkFirst(), plan, state, lease));

        Assert.True(persistence.HasPending);
        Assert.Equal(TenNinety.WpStatus.Pending, plan.WorkPackages[0].Status);
        Assert.False(state.Attempts.ContainsKey("WP-001"));
        var headAtInterruption = fixture.Git.HeadSha();
        var unrelated = Path.Combine(fixture.Root, "operator-edit.txt");
        File.WriteAllText(unrelated, "preserve me\n");
        persistence.Failpoint = null;

        var refusal = Assert.Throws<InvalidOperationException>(() =>
            persistence.RecoverPending(lease));

        Assert.Contains("preserved", refusal.Message);
        Assert.True(persistence.HasPending);
        Assert.Equal("preserve me\n", File.ReadAllText(unrelated));
        Assert.Equal(headAtInterruption, fixture.Git.HeadSha());

        File.Delete(unrelated);
        Assert.True(persistence.RecoverPending(lease));
        Assert.False(persistence.RecoverPending(lease));
        Assert.False(persistence.HasPending);
        Assert.True(fixture.Git.IsClean());
        Assert.Single(fixture.Git.RecentCommits(10), commit =>
            commit.Subject == PivotPersistence.CommitMessage);
        Assert.Equal(TenNinety.WpStatus.Pending,
            fixture.States.Load().QueueStatus["WP-001"]);
    }

    [Fact]
    public void Approved_pivot_persists_keep_rework_cancel_and_new_package_as_one_pair()
    {
        using var fixture = new PivotFixture();
        var plan = fixture.Plans.Load();
        var state = fixture.States.Load();
        var keptAttempt = state.Attempts["WP-001"];
        var proposal = new PivotProposal
        {
            Keep = { "WP-001" },
            Rework =
            {
                new PivotRework
                {
                    Id = "WP-002",
                    Reason = "requirements changed",
                    UpdatedDirectives = { "rework package two" },
                },
            },
            Cancel = { new PivotCancel { Id = "WP-003", Reason = "no longer needed" } },
            NewWorkPackages =
            {
                TestPlans.Wp("WP-900", "API", "WP-002"),
            },
        };

        using var lease = DaemonLock.Acquire(fixture.Root);
        var result = fixture.Persistence().ApplyApproved(proposal, plan, state, lease);

        Assert.Equal(2, result.Kept);
        Assert.Equal(["WP-002"], result.Reworked);
        Assert.Equal(["WP-003"], result.Cancelled);
        Assert.Equal(["WP-900"], result.Added);
        Assert.Equal(keptAttempt.Count, state.Attempts["WP-001"].Count);
        Assert.Equal(keptAttempt.Total, state.Attempts["WP-001"].Total);
        Assert.Equal(keptAttempt.Feedback, state.Attempts["WP-001"].Feedback);
        Assert.False(state.Attempts.ContainsKey("WP-002"));
        Assert.False(state.Attempts.ContainsKey("WP-003"));
        Assert.Equal(TenNinety.WpStatus.Done, state.QueueStatus["WP-001"]);
        Assert.Equal(TenNinety.WpStatus.Pending, state.QueueStatus["WP-002"]);
        Assert.Equal(TenNinety.WpStatus.Cancelled, state.QueueStatus["WP-003"]);
        Assert.Equal(TenNinety.WpStatus.Pending, state.QueueStatus["WP-900"]);
        Assert.Equal(["rework package two"],
            plan.WorkPackages.Single(wp => wp.Id == "WP-002").Directives);
        Assert.False(fixture.Persistence().HasPending);
        Assert.True(fixture.Git.IsClean());

        var persistedPlan = fixture.Plans.Load();
        var persistedState = fixture.States.Load();
        Assert.Equal(plan.WorkPackages.Select(wp => (wp.Id, wp.Status)),
            persistedPlan.WorkPackages.Select(wp => (wp.Id, wp.Status)));
        Assert.Equal(state.QueueStatus, persistedState.QueueStatus);
    }

    [Fact]
    public async Task Startup_recovers_a_mixed_rework_pair_before_terminal_status_hydration_can_skip_it()
    {
        using var fixture = new PivotFixture();
        var persistence = fixture.Persistence();
        persistence.Failpoint = phase =>
        {
            if (phase == PivotPersistencePhase.PlanWritten)
                throw new IOException("process stopped after plan replacement");
        };
        using (var lease = DaemonLock.Acquire(fixture.Root))
        {
            Assert.Throws<InvalidOperationException>(() => persistence.ApplyApproved(
                ReworkFirst(), fixture.Plans.Load(), fixture.States.Load(), lease));
        }

        var mixedPlan = fixture.Plans.Load();
        var mixedState = fixture.States.Load();
        Assert.Equal(TenNinety.WpStatus.Pending, mixedPlan.WorkPackages[0].Status);
        Assert.Equal(TenNinety.WpStatus.Done, mixedState.QueueStatus["WP-001"]);
        var audit = new AuditLog(Path.Combine(
            fixture.Root, TenNinety.StateDir, TenNinety.AuditFile));
        var orchestrator = new Orchestrator(
            fixture.Git, mixedPlan, mixedState, new TenNinetyConfig(),
            new MockFrontierClient(), fixture.States, audit);
        orchestrator.RecoveryOverride = _ => Task.FromResult(new SandboxRecoveryInfo
        {
            Status = "clean",
            LastRunUtc = DateTimeOffset.UtcNow.ToString("O"),
            Detail = "test recovery",
        });

        var outcome = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(OrchestratorExit.Completed, outcome);
        Assert.False(fixture.Persistence().HasPending);
        Assert.Contains(audit.ReadTail(100), entry =>
            entry.Event == "WP_PROMOTED" && entry.WorkPackageId == "WP-001");
        Assert.Equal(TenNinety.WpStatus.Done,
            fixture.States.Load().QueueStatus["WP-001"]);
    }

    [Fact]
    public async Task Non_main_startup_cleans_sandbox_but_does_not_rewrite_a_pending_pivot_pair()
    {
        using var fixture = new PivotFixture();
        var persistence = fixture.Persistence();
        persistence.Failpoint = phase =>
        {
            if (phase == PivotPersistencePhase.PlanWritten)
                throw new IOException("process stopped after plan replacement");
        };
        using (var lease = DaemonLock.Acquire(fixture.Root))
        {
            Assert.Throws<InvalidOperationException>(() => persistence.ApplyApproved(
                ReworkFirst(), fixture.Plans.Load(), fixture.States.Load(), lease));
        }
        fixture.Git.CreateAndCheckoutBranch("operator-branch");
        var stateBefore = File.ReadAllBytes(fixture.States.Path);
        var cleanupRan = false;
        var orchestrator = new Orchestrator(
            fixture.Git, fixture.Plans.Load(), fixture.States.Load(),
            new TenNinetyConfig(), new MockFrontierClient(), fixture.States,
            new AuditLog(Path.Combine(
                fixture.Root, TenNinety.StateDir, TenNinety.AuditFile)));
        orchestrator.RecoveryOverride = _ =>
        {
            cleanupRan = true;
            return Task.FromResult(new SandboxRecoveryInfo
            {
                Status = "clean",
                LastRunUtc = DateTimeOffset.UtcNow.ToString("O"),
                Detail = "test cleanup",
            });
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RunAsync(CancellationToken.None));

        Assert.True(cleanupRan);
        Assert.Contains("pivot recovery requires branch 'main'", error.ToString());
        Assert.Equal(stateBefore, File.ReadAllBytes(fixture.States.Path));
        Assert.True(fixture.Persistence().HasPending);
        Assert.Equal("operator-branch", fixture.Git.CurrentBranch());
    }

    [Fact]
    public async Task Cli_resume_recovers_a_pending_pivot_before_clearing_pause_flags()
    {
        using var fixture = new PivotFixture();
        fixture.States.Update(current =>
        {
            current.Paused = true;
            current.StopRequested = true;
        });
        var persistence = fixture.Persistence();
        persistence.Failpoint = phase =>
        {
            if (phase == PivotPersistencePhase.PlanWritten)
                throw new IOException("process stopped after plan replacement");
        };
        using (var lease = DaemonLock.Acquire(fixture.Root))
        {
            Assert.Throws<InvalidOperationException>(() => persistence.ApplyApproved(
                ReworkFirst(), fixture.Plans.Load(), fixture.States.Load(), lease));
        }
        ExecutionControl.SetPause(fixture.Root);
        ExecutionControl.SetStop(fixture.Root);

        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(fixture.Root);
            Assert.Equal(0, await Program.Main(["resume"]));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }

        Assert.False(fixture.Persistence().HasPending);
        Assert.Equal((false, false), ExecutionControl.ReadFlags(fixture.Root));
        var state = fixture.States.Load();
        Assert.False(state.Paused);
        Assert.False(state.StopRequested);
        Assert.Equal(TenNinety.WpStatus.Pending, state.QueueStatus["WP-001"]);
        Assert.False(state.Attempts.ContainsKey("WP-001"));
        Assert.Equal(["reworked directive"],
            fixture.Plans.Load().WorkPackages[0].Directives);
    }

    [Fact]
    public void Pivot_recovery_is_owned_by_the_linked_worktree_that_holds_its_journal()
    {
        using var fixture = new PivotFixture();
        using var linkedParent = new TempDir();
        var linkedRoot = Path.Combine(linkedParent.Root, "checkout");
        fixture.Git.CreateAndCheckoutBranch("primary-idle");
        fixture.Git.RunForTest("worktree", "add", linkedRoot, TenNinety.MainBranch);
        try
        {
            var linkedPlans = new PlanStore(Path.Combine(
                linkedRoot, TenNinety.StateDir, TenNinety.PlanFile));
            var linkedStates = new StateStore(Path.Combine(
                linkedRoot, TenNinety.StateDir, TenNinety.StateFile));
            var linkedState = fixture.States.Load();
            linkedStates.Save(linkedState);
            var linkedPersistence = new PivotPersistence(
                new GitService(linkedRoot), linkedPlans, linkedStates)
            {
                Failpoint = phase =>
                {
                    if (phase == PivotPersistencePhase.PlanWritten)
                        throw new IOException("linked interruption");
                },
            };
            using (var linkedLease = DaemonLock.Acquire(linkedRoot))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    linkedPersistence.ApplyApproved(
                        ReworkFirst(), linkedPlans.Load(), linkedState, linkedLease));
            }

            var primaryPlanBefore = File.ReadAllBytes(fixture.Plans.Path);
            var primaryStateBefore = File.ReadAllBytes(fixture.States.Path);
            Assert.Contains(Path.GetFullPath(linkedRoot), fixture.Git.WorktreePaths());
            Assert.Equal(1,
                RuntimeGitignoreMigration.CountPivotJournalsInOtherWorktrees(fixture.Git));
            using var primaryLease = DaemonLock.Acquire(fixture.Root);
            var error = Assert.Throws<InvalidOperationException>(() =>
                fixture.Persistence().RecoverPending(primaryLease));

            Assert.Contains("linked worktree", error.Message);
            Assert.Equal(primaryPlanBefore, File.ReadAllBytes(fixture.Plans.Path));
            Assert.Equal(primaryStateBefore, File.ReadAllBytes(fixture.States.Path));
            Assert.True(File.Exists(Path.Combine(
                linkedRoot, TenNinety.StateDir, TenNinety.PivotFile)));
        }
        finally
        {
            fixture.Git.RunForTest("worktree", "remove", "--force", linkedRoot);
        }
    }

    private static PivotProposal ReworkFirst() => new()
    {
        Keep = { "WP-002", "WP-003" },
        Rework =
        {
            new PivotRework
            {
                Id = "WP-001",
                Reason = "requirements changed",
                UpdatedDirectives = { "reworked directive" },
            },
        },
    };

    private sealed class PivotFixture : IDisposable
    {
        private readonly TestGitRepo _repo = new();
        public string Root => _repo.Root;
        public GitService Git => _repo.Git;
        public PlanStore Plans { get; }
        public StateStore States { get; }

        public PivotFixture()
        {
            _repo.WriteFile(".tenninety/.gitignore", RuntimeGitignoreMigration.Contents);
            _repo.WriteFile("README.md", "pivot fixture\n");
            Plans = new PlanStore(Path.Combine(
                Root, TenNinety.StateDir, TenNinety.PlanFile));
            States = new StateStore(Path.Combine(
                Root, TenNinety.StateDir, TenNinety.StateFile));
            var plan = TestPlans.Simple();
            var state = new RuntimeState();
            foreach (var wp in plan.WorkPackages)
            {
                wp.Status = TenNinety.WpStatus.Done;
                state.QueueStatus[wp.Id] = TenNinety.WpStatus.Done;
                state.Attempts[wp.Id] = new AttemptInfo
                {
                    Count = 2,
                    Total = 3,
                    Feedback = [$"progress for {wp.Id}"],
                };
            }
            Plans.Save(plan);
            States.Save(state);
            Git.CommitPaths(
                [".tenninety/.gitignore", ".tenninety/plan.json", "README.md"],
                "initial");
        }

        public PivotPersistence Persistence() => new(Git, Plans, States);

        public void Dispose() => _repo.Dispose();
    }
}
