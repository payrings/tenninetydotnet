using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>Scripted fakes for deterministic engine tests.</summary>
public sealed class SpyCoder : ICoderAgent
{
    public readonly List<WpContext> Contexts = new();

    public Task<CoderResult> ImplementAsync(WpContext ctx, CancellationToken ct = default)
    {
        Contexts.Add(ctx);
        var file = System.IO.Path.Combine(ctx.RepoPath, "app", $"{ctx.WorkPackage.Id}.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"attempt {ctx.Attempt}\n");
        return Task.FromResult(new CoderResult { ProducedChanges = true, Summary = "spy change" });
    }
}

public sealed class ScriptedReviewer : IReviewerAgent
{
    private readonly int _failAttempts;

    /// <param name="failAttempts">
    /// Number of failing reviews, counted via the WP's accumulated feedback entries. Counting feedback
    /// (not phase counters) keeps behavior deterministic across escalation-driven counter resets.
    /// </param>
    public ScriptedReviewer(int failAttempts) => _failAttempts = failAttempts;

    public Task<ReviewResult> ReviewAsync(WpContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new ReviewResult
        {
            Passed = ctx.Feedback.Count >= _failAttempts,
            Reasons = ctx.Feedback.Count >= _failAttempts ? new List<string>() : new List<string> { "not good enough yet" },
            ReviewerModel = "scripted",
        });
}

public sealed class ScriptedTester : ITesterAgent
{
    private readonly int _failAttempts;
    public ScriptedTester(int failAttempts) => _failAttempts = failAttempts;

    public Task<TestRunResult> RunTestsAsync(WpContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new TestRunResult
        {
            Passed = ctx.Attempt > _failAttempts,
            ExitCode = ctx.Attempt > _failAttempts ? 0 : 1,
            OutputTail = "simulated test failure",
            Command = "(scripted)",
        });
}

public sealed class EngineHarness : IDisposable
{
    public TempDir Dir { get; } = new();
    public GitService Git { get; }
    public StateStore States { get; }
    public AuditLog Audit { get; }
    public TenNinetyConfig Config { get; }
    public RuntimeState State { get; } = new();
    public Plan Plan { get; }
    public SpyCoder Coder { get; } = new();

    public EngineHarness(int maxAttempts = 3, int maxTotal = 6)
    {
        Git = new GitService(Dir.Root);
        Git.Init();
        // Mirror the real workspace layout: runtime contracts live under .tenninety/ and stay untracked.
        File.WriteAllText(System.IO.Path.Combine(Dir.Root, ".gitignore"), ".tenninety/\n");
        Directory.CreateDirectory(System.IO.Path.Combine(Dir.Root, ".tenninety"));
        File.WriteAllText(System.IO.Path.Combine(Dir.Root, "README.md"), "demo\n");
        Git.CommitAll("initial");

        Config = new TenNinetyConfig
        {
            MaxAttemptsBeforeEscalation = maxAttempts,
            MaxTotalAttempts = maxTotal,
            Mock = new MockBehaviorConfig(),
        };
        States = new StateStore(System.IO.Path.Combine(Dir.Root, ".tenninety", "state.json"));
        Audit = new AuditLog(System.IO.Path.Combine(Dir.Root, ".tenninety", "audit-log.jsonl"));
        Plan = TestPlans.Simple();
        foreach (var wp in Plan.WorkPackages)
            State.QueueStatus[wp.Id] = wp.Status;
    }

    public ExecutionEngine CreateEngine(IReviewerAgent? reviewer = null, ITesterAgent? tester = null) =>
        new(Git, Config, new MockFrontierClient(), Coder,
            reviewer ?? new ScriptedReviewer(0), tester ?? new ScriptedTester(0),
            States, Audit);

    public void Dispose() => Dir.Dispose();
}

public class ExecutionEngineTests
{
    [Fact]
    public async Task Reviewer_failures_loop_back_then_promote_on_pass()
    {
        using var h = new EngineHarness(maxAttempts: 5);
        var wp = h.Plan.WorkPackages[0];
        var engine = h.CreateEngine(reviewer: new ScriptedReviewer(failAttempts: 2));

        var outcome = await engine.ExecuteWpAsync(wp, h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Done, outcome);
        Assert.Equal(TenNinety.WpStatus.Done, wp.Status);
        Assert.Equal(3, h.Coder.Contexts.Count); // two review failures then success

        // Feedback accumulated across attempts reached the coder.
        Assert.Contains(h.Coder.Contexts[2].Feedback, f => f.Contains("not good enough yet"));

        // Promotion merged the work into main.
        Assert.Equal("main", h.Git.CurrentBranch());
        Assert.True(h.Git.IsClean());
        Assert.False(h.Git.BranchExists("work/WP-001"));

        var events = h.Audit.ReadTail(100).Select(e => e.Event).ToList();
        Assert.Contains("REVIEW_FAILED", events);
        Assert.Contains("REVIEW_PASSED", events);
        Assert.Contains("WP_PROMOTED", events);
    }

    [Fact]
    public async Task Tester_failure_feeds_logs_into_next_attempt_context()
    {
        using var h = new EngineHarness(maxAttempts: 5);
        var engine = h.CreateEngine(tester: new ScriptedTester(failAttempts: 1));

        await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        Assert.Equal(2, h.Coder.Contexts.Count);
        Assert.Contains(h.Coder.Contexts[1].Feedback,
            f => f.StartsWith("[tester]") && f.Contains("simulated test failure"));
        // The failure was also recorded in the audit trail (attempts entry is cleared on success).
        Assert.Contains(h.Audit.ReadTail(100), e => e.Event == "TESTS_FAILED" && e.WorkPackageId == "WP-001");
    }

    [Fact]
    public async Task Attempt_budget_escalates_to_frontier_then_resets_counter()
    {
        using var h = new EngineHarness(maxAttempts: 3, maxTotal: 10);
        var engine = h.CreateEngine(reviewer: new ScriptedReviewer(failAttempts: 3));

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Done, outcome);
        Assert.False(h.States.Load().Attempts.ContainsKey("WP-001")); // cleared on success

        var adviceEvent = h.Audit.ReadTail(100).First(e => e.Event == "ESCALATION_ADVICE");
        Assert.Equal("WP-001", adviceEvent.WorkPackageId);
        Assert.Single(h.Audit.ReadTail(100), e => e.Event == "ESCALATION_ADVICE");
        Assert.Equal(4, h.Coder.Contexts.Count); // 3 failures + 1 post-advice success
        Assert.NotEmpty(h.Coder.Contexts[3].Advice); // frontier advice was injected into coder context
    }

    [Fact]
    public async Task Exhausted_total_budget_marks_wp_blocked_and_notifies()
    {
        using var h = new EngineHarness(maxAttempts: 2, maxTotal: 4);
        var logs = new List<string>();
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), h.Coder,
            new AlwaysFailReviewer(), new ScriptedTester(0),
            h.States, h.Audit, globalContext: null, logs.Add);

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Blocked, outcome);
        var wp = h.Plan.WorkPackages[0];
        Assert.Equal(TenNinety.WpStatus.Blocked, wp.Status);
        Assert.Equal(TenNinety.WpStatus.Blocked, h.State.QueueStatus["WP-001"]);
        Assert.Equal(4, h.Coder.Contexts.Count); // exactly MaxTotalAttempts attempts
        Assert.Contains(logs, l => l.Contains("BLOCKED after 4 attempts"));
        Assert.Contains(h.Audit.ReadTail(50).Select(e => e.Event), e => e == "WP_BLOCKED");
        Assert.Equal("main", h.Git.CurrentBranch()); // never stranded on the work branch
    }

    [Fact]
    public async Task Pause_between_attempts_saves_state_and_reports_paused()
    {
        using var h = new EngineHarness();
        h.State.Paused = true;
        var engine = h.CreateEngine();

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Paused, outcome);
        Assert.Empty(h.Coder.Contexts); // no attempt consumed
        Assert.Equal(TenNinety.WpStatus.Pending, h.Plan.WorkPackages[0].Status);
    }

    [Fact]
    public async Task Reviewer_infrastructure_exception_aborts_with_resumable_state()
    {
        using var h = new EngineHarness();
        var wp = h.Plan.WorkPackages[0];
        var engine = h.CreateEngine(reviewer: new ThrowingReviewer());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ExecuteWpAsync(wp, h.State, CancellationToken.None));

        Assert.Equal("main", h.Git.CurrentBranch());
        Assert.True(h.Git.BranchExists("work/WP-001"));
        Assert.Equal(1, h.States.Load().Attempts["WP-001"].Total);
        Assert.Equal(TenNinety.WpStatus.Pending, wp.Status);
        Assert.Null(h.States.Load().CurrentWp);
    }

    [Fact]
    public async Task Passing_tester_cannot_mutate_files_after_review()
    {
        using var h = new EngineHarness();
        var engine = h.CreateEngine(tester: new MutatingPassingTester());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None));

        Assert.Contains("after the reviewed commit", ex.Message);
        Assert.DoesNotContain(h.Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.True(h.Git.BranchExists("work/WP-001"));
        Assert.Equal("main", h.Git.CurrentBranch());
    }

    [Fact]
    public async Task Cancelling_a_coder_checkpoints_partial_work_off_main()
    {
        using var h = new EngineHarness();
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), new BlockingPartialCoder(),
            new ScriptedReviewer(0), new ScriptedTester(0), h.States, h.Audit);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, cts.Token);

        Assert.Equal(WpOutcome.Stopped, outcome);
        Assert.Equal("main", h.Git.CurrentBranch());
        Assert.True(h.Git.IsClean());
        Assert.False(File.Exists(System.IO.Path.Combine(h.Dir.Root, "partial.txt")));
        Assert.Contains("interrupted checkpoint", h.Git.FindCommit("work/WP-001")!.Subject);
    }

    private sealed class AlwaysFailReviewer : IReviewerAgent
    {
        public Task<ReviewResult> ReviewAsync(WpContext ctx, CancellationToken ct = default) =>
            Task.FromResult(new ReviewResult { Passed = false, Reasons = { "hopeless" } });
    }

    private sealed class ThrowingReviewer : IReviewerAgent
    {
        public Task<ReviewResult> ReviewAsync(WpContext ctx, CancellationToken ct = default) =>
            throw new InvalidOperationException("review service unavailable");
    }

    private sealed class MutatingPassingTester : ITesterAgent
    {
        public Task<TestRunResult> RunTestsAsync(WpContext ctx, CancellationToken ct = default)
        {
            File.WriteAllText(System.IO.Path.Combine(ctx.RepoPath, "post-review.txt"), "unreviewed");
            return Task.FromResult(new TestRunResult { Passed = true, ExitCode = 0 });
        }
    }

    private sealed class BlockingPartialCoder : ICoderAgent
    {
        public async Task<CoderResult> ImplementAsync(WpContext ctx, CancellationToken ct = default)
        {
            File.WriteAllText(System.IO.Path.Combine(ctx.RepoPath, "partial.txt"), "partial");
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new CoderResult();
        }
    }
}

public class BranchLifecycleRegressionTests
{
    [Fact]
    public async Task Paused_then_resumed_reuses_the_existing_work_branch()
    {
        // External review Major 2 regression: pause after branch creation used to crash
        // resume with "a branch named 'work/WP-001' already exists".
        using var h = new EngineHarness(maxAttempts: 5);
        var wp = h.Plan.WorkPackages[0];
        var engine = h.CreateEngine();

        h.State.Paused = true;
        var first = await engine.ExecuteWpAsync(wp, h.State, CancellationToken.None);
        Assert.Equal(WpOutcome.Paused, first);
        Assert.True(h.Git.BranchExists("work/WP-001"));

        h.State.Paused = false;
        var second = await engine.ExecuteWpAsync(wp, h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Done, second); // would throw before the fix
        Assert.Equal(TenNinety.WpStatus.Done, wp.Status);
    }

    [Fact]
    public async Task Resumed_branch_integrates_an_advanced_main_before_testing()
    {
        using var h = new EngineHarness(maxAttempts: 2, maxTotal: 3);
        var wp = h.Plan.WorkPackages[0];
        h.State.Paused = true;
        Assert.Equal(WpOutcome.Paused,
            await h.CreateEngine().ExecuteWpAsync(wp, h.State, CancellationToken.None));

        File.WriteAllText(System.IO.Path.Combine(h.Dir.Root, "main-after-pause.txt"), "required");
        h.Git.CommitAll("advance main while paused");
        h.State.Paused = false;
        var engine = h.CreateEngine(tester: new RequiresFileTester("main-after-pause.txt"));

        var outcome = await engine.ExecuteWpAsync(wp, h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Done, outcome);
    }

    private sealed class RequiresFileTester(string fileName) : ITesterAgent
    {
        public Task<TestRunResult> RunTestsAsync(WpContext ctx, CancellationToken ct = default)
        {
            var exists = File.Exists(System.IO.Path.Combine(ctx.RepoPath, fileName));
            return Task.FromResult(new TestRunResult
            {
                Passed = exists,
                ExitCode = exists ? 0 : 1,
                OutputTail = exists ? "present" : "required main file missing",
            });
        }
    }

    [Fact]
    public async Task Starting_from_a_non_base_branch_is_refused()
    {
        // External review Major 8: a promotion could drag an unrelated feature branch onto main.
        using var h = new EngineHarness(maxAttempts: 3);
        h.Git.CreateAndCheckoutBranch("feature/unrelated");
        File.WriteAllText(System.IO.Path.Combine(h.Dir.Root, "unrelated.txt"), "x");
        h.Git.CommitAll("unrelated feature work");

        var logs = new List<string>();
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), h.Coder,
            new ScriptedReviewer(0), new ScriptedTester(0),
            h.States, h.Audit, globalContext: null, logs.Add);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None));

        Assert.Contains("must start from branch 'main'", ex.Message);
        Assert.Equal("feature/unrelated", h.Git.CurrentBranch()); // untouched
        Assert.Null(h.State.CurrentWp);
        Assert.Equal(TenNinety.WpStatus.Pending, h.Plan.WorkPackages[0].Status);
    }
}
