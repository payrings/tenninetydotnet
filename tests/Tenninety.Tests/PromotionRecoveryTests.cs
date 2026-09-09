using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Sandbox;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Tests;

public sealed class PromotionRecoveryTests
{
    [Theory]
    [InlineData(RecoveryBoundary.Prepared)]
    [InlineData(RecoveryBoundary.MainPublished)]
    [InlineData(RecoveryBoundary.ProgressSaved)]
    [InlineData(RecoveryBoundary.BranchDeleted)]
    public async Task Restart_completes_each_promotion_boundary_without_coding_again(
        RecoveryBoundary boundary)
    {
        using var fixture = new RecoveryFixture();
        var transaction = fixture.Prepare(boundary);
        var state = fixture.States.Load();
        var plan = fixture.Plan();
        var orchestrator = fixture.Orchestrator(plan, state);

        var result = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(OrchestratorExit.Completed, result);
        Assert.Equal(transaction.PromotionSha, fixture.Git.HeadSha());
        Assert.Equal(TenNinety.WpStatus.Done, state.QueueStatus["WP-001"]);
        Assert.False(fixture.Git.BranchExists(transaction.Branch));
        Assert.False(fixture.Promotions.Exists());
        Assert.DoesNotContain(fixture.Audit.ReadTail(100), entry => entry.Event == "WP_STARTED");
        Assert.Contains(fixture.Audit.ReadTail(100), entry => entry.Event == "PROMOTION_RECOVERED");
    }

    [Fact]
    public async Task Promotion_recovery_is_idempotent()
    {
        using var fixture = new RecoveryFixture();
        var transaction = fixture.Prepare(RecoveryBoundary.MainPublished);
        var firstPlan = fixture.Plan();
        var firstState = fixture.States.Load();

        Assert.Equal(OrchestratorExit.Completed,
            await fixture.Orchestrator(firstPlan, firstState).RunAsync(CancellationToken.None));
        Assert.Equal(OrchestratorExit.Completed,
            await fixture.Orchestrator(fixture.Plan(), fixture.States.Load())
                .RunAsync(CancellationToken.None));

        Assert.Equal(transaction.PromotionSha, fixture.Git.HeadSha());
        Assert.Single(fixture.Audit.ReadTail(100), entry => entry.Event == "PROMOTION_RECOVERED");
        Assert.False(fixture.Promotions.Exists());
    }

    [Fact]
    public async Task Hotfix_recovery_does_not_require_a_valid_work_package_plan()
    {
        using var fixture = new RecoveryFixture();
        const string branch = "hotfix/revert-deadbeef";
        var baseSha = fixture.Git.HeadSha();
        fixture.Git.CreateAndCheckoutBranch(branch);
        File.WriteAllText(Path.Combine(fixture.Root, "hotfix.txt"), "candidate\n");
        var candidate = fixture.Git.CommitAll("hotfix candidate")!;
        var promotion = fixture.Git.PrepareSquashPromotion(
            branch, baseSha, candidate, "Revert test [hotfix]");
        var transaction = new PromotionTransaction
        {
            Kind = TenNinety.PromotionKinds.Hotfix,
            ExecutionId = Guid.NewGuid().ToString("N"),
            Branch = branch,
            ExpectedBaseSha = baseSha,
            CandidateSha = candidate,
            PromotionSha = promotion,
        };
        fixture.Promotions.Save(transaction);

        var result = await fixture.Orchestrator(
            new Plan { ProjectName = "recovery only" }, new RuntimeState())
            .RunAsync(CancellationToken.None);

        Assert.Equal(OrchestratorExit.Completed, result);
        Assert.Equal(promotion, fixture.Git.HeadSha());
        Assert.False(fixture.Git.BranchExists(branch));
        Assert.False(fixture.Promotions.Exists());
    }

    [Fact]
    public async Task Failed_progress_persistence_retains_evidence_and_restart_marks_done()
    {
        using var fixture = new RecoveryFixture();
        var state = fixture.PendingState();
        fixture.States.Save(state);
        var plan = fixture.Plan();
        var failDoneSave = true;
        fixture.States.BeforeSave = saved =>
        {
            if (failDoneSave && saved.QueueStatus.GetValueOrDefault("WP-001") == TenNinety.WpStatus.Done)
            {
                failDoneSave = false;
                throw new IOException("simulated progress persistence failure");
            }
        };
        var coder = new SequenceCoder(fixture.Root);
        var engine = new ExecutionEngine(
            fixture.Git, new TenNinetyConfig(), new MockFrontierClient(), coder,
            new ScriptedReviewer(0), new ScriptedTester(0), fixture.States, fixture.Audit);

        await Assert.ThrowsAsync<IOException>(() =>
            engine.ExecuteWpAsync(plan.WorkPackages[0], state, CancellationToken.None));

        var persisted = fixture.States.Load();
        Assert.Equal(TenNinety.WpStatus.Active, persisted.QueueStatus["WP-001"]);
        Assert.NotNull(persisted.CurrentWp);
        Assert.True(fixture.Promotions.Exists());
        Assert.True(fixture.Git.BranchExists("work/WP-001"));
        Assert.Equal(1, coder.Calls);

        fixture.States.BeforeSave = null;
        Assert.Equal(OrchestratorExit.Completed,
            await fixture.Orchestrator(fixture.Plan(), persisted)
                .RunAsync(CancellationToken.None));

        Assert.Equal(1, coder.Calls);
        Assert.Equal(TenNinety.WpStatus.Done,
            fixture.States.Load().QueueStatus["WP-001"]);
        Assert.False(fixture.Promotions.Exists());
    }

    [Fact]
    public async Task Conflicting_main_is_quarantined_without_rewriting_or_losing_evidence()
    {
        using var fixture = new RecoveryFixture();
        var transaction = fixture.Prepare(RecoveryBoundary.Prepared);
        fixture.Git.CheckoutBranch(TenNinety.MainBranch);
        File.WriteAllText(Path.Combine(fixture.Root, "external.txt"), "external\n");
        var externalMain = fixture.Git.CommitAll("external main update")!;
        fixture.Git.CheckoutBranch(transaction.Branch);
        var cleanupRan = false;
        var orchestrator = fixture.Orchestrator(fixture.Plan(), fixture.States.Load(), () => cleanupRan = true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RunAsync(CancellationToken.None));

        Assert.True(cleanupRan);
        Assert.Contains("could not be proven safe", error.Message);
        Assert.Equal(externalMain, fixture.Git.FindCommit(TenNinety.MainBranch)!.Sha);
        Assert.Equal(transaction.CandidateSha, fixture.Git.FindCommit(transaction.Branch)!.Sha);
        Assert.True(fixture.Promotions.Exists());
        Assert.Equal(TenNinety.WpStatus.Active,
            fixture.States.Load().QueueStatus["WP-001"]);
    }

    [Fact]
    public async Task Startup_refuses_a_promotion_journal_owned_by_another_linked_worktree()
    {
        using var fixture = new RecoveryFixture();
        using var linkedParent = new TempDir();
        var linkedPath = Path.Combine(linkedParent.Root, "checkout");
        fixture.Git.RunForTest(
            "worktree", "add", "-b", "linked-runtime", linkedPath, TenNinety.MainBranch);
        try
        {
            Directory.CreateDirectory(Path.Combine(linkedPath, TenNinety.StateDir));
            File.WriteAllText(Path.Combine(
                linkedPath, TenNinety.StateDir, TenNinety.PromotionFile), "{}\n");
            var orchestrator = fixture.Orchestrator(
                fixture.Plan(), fixture.PendingState());

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                orchestrator.RunAsync(CancellationToken.None));

            Assert.Contains("linked worktree promotion", error.Message);
            Assert.Equal(TenNinety.MainBranch, fixture.Git.CurrentBranch());
        }
        finally
        {
            fixture.Git.RunForTest("worktree", "remove", "--force", linkedPath);
        }
    }

    [Fact]
    public async Task Engine_never_checkpoints_external_changes_after_promotion_is_recorded()
    {
        using var fixture = new RecoveryFixture();
        var state = fixture.PendingState();
        fixture.States.Save(state);
        var plan = fixture.Plan();
        var coder = new SequenceCoder(fixture.Root);
        fixture.Git.SquashPromotionFailpoint = phase =>
        {
            if (phase != SquashPromotionPhase.AfterMainCompareAndSwap) return;
            File.WriteAllText(Path.Combine(fixture.Root, "external-untracked.txt"), "preserve\n");
            throw new IOException("simulated post-publication failure");
        };
        var engine = new ExecutionEngine(
            fixture.Git, new TenNinetyConfig(), new MockFrontierClient(), coder,
            new ScriptedReviewer(0), new ScriptedTester(0), fixture.States, fixture.Audit);

        await Assert.ThrowsAsync<SquashPromotionException>(() =>
            engine.ExecuteWpAsync(plan.WorkPackages[0], state, CancellationToken.None));

        var transaction = Assert.IsType<PromotionTransaction>(fixture.Promotions.Load());
        Assert.Equal(transaction.CandidateSha,
            fixture.Git.FindCommit(transaction.Branch)!.Sha);
        Assert.Equal("preserve\n", File.ReadAllText(
            Path.Combine(fixture.Root, "external-untracked.txt")));
        Assert.DoesNotContain("external-untracked.txt",
            System.Text.Encoding.UTF8.GetString(
                fixture.Git.LsTreeRecursiveRaw(transaction.CandidateSha, 1024 * 1024)));
        Assert.True(fixture.Promotions.Exists());
    }

    [Fact]
    public async Task Rework_of_a_completed_id_gets_a_new_execution_and_is_not_inferred_done()
    {
        using var fixture = new RecoveryFixture();
        var plan = fixture.Plan();
        var state = fixture.PendingState();
        fixture.States.Save(state);
        var coder = new SequenceCoder(fixture.Root);
        var engine = new ExecutionEngine(
            fixture.Git, new TenNinetyConfig(), new MockFrontierClient(), coder,
            new ScriptedReviewer(0), new ScriptedTester(0), fixture.States, fixture.Audit);

        Assert.Equal(WpOutcome.Done,
            await engine.ExecuteWpAsync(plan.WorkPackages[0], state, CancellationToken.None));
        PivotService.Apply(new PivotProposal
        {
            Rework =
            {
                new PivotRework
                {
                    Id = "WP-001",
                    Reason = "requirements changed",
                    UpdatedDirectives = { "implement the revised behavior" },
                },
            },
        }, plan, state);
        fixture.States.Save(state);

        Assert.Equal(WpOutcome.Done,
            await engine.ExecuteWpAsync(plan.WorkPackages[0], state, CancellationToken.None));

        Assert.Equal(2, coder.Calls);
        var executions = fixture.Audit.ReadTail(200)
            .Where(entry => entry.Event == "WP_PROMOTION_PREPARED")
            .Select(entry => entry.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();
        Assert.Equal(2, executions.Count);
        Assert.Equal(2, executions.Distinct(StringComparer.Ordinal).Count());
        Assert.False(fixture.Promotions.Exists());
    }

    [Fact]
    public void Legacy_active_state_does_not_infer_completion_from_subject_or_branch_absence()
    {
        using var fixture = new RecoveryFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "legacy.txt"), "already promoted\n");
        fixture.Git.CommitAll("WP-001: legacy work [work package]");
        var state = fixture.PendingState();
        state.QueueStatus["WP-001"] = TenNinety.WpStatus.Active;

        var orchestrator = fixture.Orchestrator(fixture.Plan(), state);

        Assert.Equal("WP-001", orchestrator.SelectNextReady()!.Id);
        Assert.False(fixture.Promotions.Exists());
    }

    public enum RecoveryBoundary
    {
        Prepared,
        MainPublished,
        ProgressSaved,
        BranchDeleted,
    }

    private sealed class SequenceCoder(string root) : ICoderAgent
    {
        public int Calls { get; private set; }

        public Task<CoderResult> ImplementAsync(
            CoderRunContext ctx, CancellationToken ct = default)
        {
            Calls++;
            File.WriteAllText(Path.Combine(root, "implementation.txt"), $"generation {Calls}\n");
            return Task.FromResult(new CoderResult
            {
                ProducedChanges = true,
                Summary = "sequence change",
            });
        }
    }

    private sealed class RecoveryFixture : IDisposable
    {
        private readonly TempDir _directory = new();
        public string Root => _directory.Root;
        public GitService Git { get; }
        public StateStore States { get; }
        public PromotionTransactionStore Promotions { get; }
        public AuditLog Audit { get; }

        public RecoveryFixture()
        {
            Git = new GitService(Root);
            Git.Init();
            Directory.CreateDirectory(Path.Combine(Root, TenNinety.StateDir));
            File.WriteAllText(Path.Combine(Root, TenNinety.StateDir, ".gitignore"),
                RuntimeGitignoreMigration.Contents);
            File.WriteAllText(Path.Combine(Root, "README.md"), "fixture\n");
            Git.CommitPaths([TenNinety.StateDir + "/.gitignore", "README.md"], "initial");
            States = new StateStore(Path.Combine(Root, TenNinety.StateDir, TenNinety.StateFile));
            Promotions = new PromotionTransactionStore(
                Path.Combine(Root, TenNinety.StateDir, TenNinety.PromotionFile));
            Audit = new AuditLog(Path.Combine(Root, TenNinety.StateDir, TenNinety.AuditFile));
        }

        public Plan Plan() => new()
        {
            ProjectName = "promotion recovery",
            WorkPackages = { TestPlans.Wp("WP-001") },
        };

        public RuntimeState PendingState() => new()
        {
            QueueStatus = { ["WP-001"] = TenNinety.WpStatus.Pending },
        };

        public PromotionTransaction Prepare(RecoveryBoundary boundary)
        {
            const string branch = "work/WP-001";
            var baseSha = Git.HeadSha();
            Git.CreateAndCheckoutBranch(branch);
            File.WriteAllText(Path.Combine(Root, "implementation.txt"), "candidate\n");
            var candidate = Git.CommitAll("candidate")!;
            var state = PendingState();
            var executionId = Guid.NewGuid().ToString("N");
            state.CurrentWp = "WP-001";
            state.QueueStatus["WP-001"] = TenNinety.WpStatus.Active;
            state.Attempts["WP-001"] = new AttemptInfo
            {
                ExecutionId = executionId,
                Count = 1,
                Total = 1,
            };
            States.Save(state);
            var promotionSha = Git.PrepareSquashPromotion(
                branch, baseSha, candidate, "WP-001: candidate [work package]");
            var transaction = new PromotionTransaction
            {
                Kind = TenNinety.PromotionKinds.WorkPackage,
                ExecutionId = executionId,
                WorkPackageId = "WP-001",
                Branch = branch,
                ExpectedBaseSha = baseSha,
                CandidateSha = candidate,
                PromotionSha = promotionSha,
            };
            Promotions.Save(transaction);
            if (boundary >= RecoveryBoundary.MainPublished)
                Git.CompleteSquashPromotion(branch, baseSha, candidate, promotionSha);
            if (boundary >= RecoveryBoundary.ProgressSaved)
            {
                state.CurrentWp = null;
                state.QueueStatus["WP-001"] = TenNinety.WpStatus.Done;
                state.Attempts.Remove("WP-001");
                States.Save(state);
            }
            if (boundary >= RecoveryBoundary.BranchDeleted)
                Git.DeleteBranchCompareAndSwap(branch, candidate);
            return transaction;
        }

        public Orchestrator Orchestrator(
            Plan plan, RuntimeState state, Action? onRecovery = null)
        {
            var orchestrator = new Orchestrator(
                Git, plan, state, new TenNinetyConfig(), new MockFrontierClient(),
                States, Audit);
            orchestrator.RecoveryOverride = _ =>
            {
                onRecovery?.Invoke();
                return Task.FromResult(new SandboxRecoveryInfo
                {
                    Status = "clean",
                    LastRunUtc = DateTimeOffset.UtcNow.ToUniversalTime().ToString("O"),
                    Detail = "test recovery completed",
                });
            };
            return orchestrator;
        }

        public void Dispose() => _directory.Dispose();
    }
}

public sealed class StartupRecoveryRegressionTests
{
    [Fact]
    public async Task Known_interrupted_work_branch_is_cleaned_then_reconciled_under_lock()
    {
        using var fixture = new StartupFixture();
        fixture.CheckoutKnownWorkBranch(dirty: false);
        var recoveredOn = "";
        var orchestrator = fixture.Orchestrator(() => recoveredOn = fixture.Git.CurrentBranch());

        var result = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(OrchestratorExit.Paused, result);
        Assert.Equal("work/WP-001", recoveredOn);
        Assert.Equal(TenNinety.MainBranch, fixture.Git.CurrentBranch());
        Assert.Equal(TenNinety.WpStatus.Pending, fixture.States.Load().QueueStatus["WP-001"]);
        Assert.True(fixture.Git.BranchExists("work/WP-001"));
    }

    [Fact]
    public async Task Unrelated_branch_is_rejected_only_after_scoped_cleanup()
    {
        using var fixture = new StartupFixture();
        fixture.Git.CreateAndCheckoutBranch("feature/unrelated");
        var cleanupRan = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator(() => cleanupRan = true).RunAsync(CancellationToken.None));

        Assert.True(cleanupRan);
        Assert.Contains("not the exact interrupted work branch", error.Message);
        Assert.Equal("feature/unrelated", fixture.Git.CurrentBranch());
    }

    [Fact]
    public async Task Dirty_interrupted_branch_is_preserved_after_cleanup_with_actionable_error()
    {
        using var fixture = new StartupFixture();
        fixture.CheckoutKnownWorkBranch(dirty: true);
        var cleanupRan = false;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Orchestrator(() => cleanupRan = true).RunAsync(CancellationToken.None));

        Assert.True(cleanupRan);
        Assert.Contains("uncommitted work", error.Message);
        Assert.Contains("commit or otherwise resolve", error.Message);
        Assert.Equal("work/WP-001", fixture.Git.CurrentBranch());
        Assert.Equal("preserve\n", File.ReadAllText(Path.Combine(fixture.Root, "dirty.txt")));
        Assert.False(fixture.Git.IsClean());
    }

    [Fact]
    public async Task Cleanup_failure_quarantines_before_any_work_branch_reconciliation()
    {
        using var fixture = new StartupFixture();
        fixture.CheckoutKnownWorkBranch(dirty: false);
        var orchestrator = fixture.Orchestrator();
        orchestrator.RecoveryOverride = _ => Task.FromResult(new SandboxRecoveryInfo
        {
            Status = "quarantined",
            LastRunUtc = DateTimeOffset.UtcNow.ToUniversalTime().ToString("O"),
            Quarantined = { "container-aaaaaaaaaaaa" },
            Detail = "test cleanup failure",
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RunAsync(CancellationToken.None));

        Assert.Equal("work/WP-001", fixture.Git.CurrentBranch());
        Assert.Equal("quarantined", fixture.States.Load().SandboxRecovery.Status);
    }

    private sealed class StartupFixture : IDisposable
    {
        private readonly TempDir _directory = new();
        public string Root => _directory.Root;
        public GitService Git { get; }
        public StateStore States { get; }
        private Plan Plan { get; } = new()
        {
            ProjectName = "startup recovery",
            WorkPackages = { TestPlans.Wp("WP-001") },
        };
        private RuntimeState State { get; } = new()
        {
            CurrentWp = "WP-001",
            Paused = true,
            QueueStatus = { ["WP-001"] = TenNinety.WpStatus.Active },
            Attempts =
            {
                ["WP-001"] = new AttemptInfo
                {
                    ExecutionId = "0123456789abcdef0123456789abcdef",
                    Count = 1,
                    Total = 1,
                },
            },
        };

        public StartupFixture()
        {
            Git = new GitService(Root);
            Git.Init();
            Directory.CreateDirectory(Path.Combine(Root, TenNinety.StateDir));
            File.WriteAllText(Path.Combine(Root, TenNinety.StateDir, ".gitignore"),
                RuntimeGitignoreMigration.Contents);
            File.WriteAllText(Path.Combine(Root, "README.md"), "fixture\n");
            Git.CommitPaths([TenNinety.StateDir + "/.gitignore", "README.md"], "initial");
            States = new StateStore(Path.Combine(Root, TenNinety.StateDir, TenNinety.StateFile));
            States.Save(State);
        }

        public void CheckoutKnownWorkBranch(bool dirty)
        {
            Git.CreateAndCheckoutBranch("work/WP-001");
            File.WriteAllText(Path.Combine(Root, "candidate.txt"), "committed\n");
            Git.CommitAll("interrupted candidate");
            if (dirty)
                File.WriteAllText(Path.Combine(Root, "dirty.txt"), "preserve\n");
        }

        public Orchestrator Orchestrator(Action? recovery = null)
        {
            var orchestrator = new Orchestrator(
                Git, Plan, State, new TenNinetyConfig(), new MockFrontierClient(),
                States, new AuditLog(Path.Combine(Root, TenNinety.StateDir, TenNinety.AuditFile)));
            orchestrator.RecoveryOverride = _ =>
            {
                recovery?.Invoke();
                return Task.FromResult(new SandboxRecoveryInfo
                {
                    Status = "clean",
                    LastRunUtc = DateTimeOffset.UtcNow.ToUniversalTime().ToString("O"),
                    Detail = "test cleanup complete",
                });
            };
            return orchestrator;
        }

        public void Dispose() => _directory.Dispose();
    }
}
