using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Testing;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>Scripted fakes for deterministic engine tests.</summary>
public sealed class SpyCoder : ICoderAgent
{
    private readonly string _repoPath;
    private readonly IGitService? _git;
    public readonly List<CoderRunContext> Contexts = new();

    public SpyCoder(string repoPath, IGitService? git = null)
    {
        _repoPath = repoPath;
        _git = git;
    }

    public Task<CoderResult> ImplementAsync(CoderRunContext ctx, CancellationToken ct = default)
    {
        Contexts.Add(ctx);
        var file = System.IO.Path.Combine(_repoPath, "app", $"{ctx.WorkPackage.Id}.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $"attempt {ctx.Attempt}\n");
        return Task.FromResult(new CoderResult
        {
            ProducedChanges = true,
            Summary = "spy change",
            CommitSha = _git?.CommitAll($"{ctx.WorkPackage.Id}: spy change [attempt {ctx.Attempt}]"),
        });
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

    public Task<ReviewResult> ReviewAsync(ReviewerRunContext ctx, CancellationToken ct = default) =>
        Task.FromResult(new ReviewResult
        {
            Passed = ctx.Feedback.Count >= _failAttempts,
            Reasons = ctx.Feedback.Count >= _failAttempts ? new List<string>() : new List<string> { "not good enough yet" },
            ReviewerModel = "scripted",
            CandidateSha = ctx.Candidate.CommitSha,
        });
}

public sealed class ScriptedTester : ITesterAgent
{
    private readonly int _failAttempts;
    public ScriptedTester(int failAttempts) => _failAttempts = failAttempts;

    public Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
    {
        ctx.Validate();
        return Task.FromResult(new TestRunResult
        {
            Passed = ctx.Attempt > _failAttempts,
            ExitCode = ctx.Attempt > _failAttempts ? 0 : 1,
            OutputTail = "simulated test failure",
            Command = "(scripted)",
            CandidateSha = ctx.Candidate.CommitSha,
        });
    }
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
    public SpyCoder Coder { get; }

    public EngineHarness(int maxAttempts = 3, int maxTotal = 6)
    {
        Git = new GitService(Dir.Root);
        Git.Init();
        // Mirror the real workspace layout: runtime contracts live under .tenninety/ and stay untracked.
        File.WriteAllText(System.IO.Path.Combine(Dir.Root, ".gitignore"), ".tenninety/\n");
        Directory.CreateDirectory(System.IO.Path.Combine(Dir.Root, ".tenninety"));
        File.WriteAllText(System.IO.Path.Combine(Dir.Root, "README.md"), "demo\n");
        Git.CommitAll("initial");
        Coder = new SpyCoder(Dir.Root);

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
        Assert.Equal(0, h.States.Load().Attempts["WP-001"].Total);
        Assert.Equal(TenNinety.WpStatus.Pending, wp.Status);
        Assert.Null(h.States.Load().CurrentWp);
    }

    [Fact]
    public async Task Passing_tester_cannot_mutate_files_after_review()
    {
        using var h = new EngineHarness();
        var engine = h.CreateEngine(tester: new MutatingPassingTester(h.Dir.Root));

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
            h.Git, h.Config, new MockFrontierClient(), new BlockingPartialCoder(h.Dir.Root),
            new ScriptedReviewer(0), new ScriptedTester(0), h.States, h.Audit);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, cts.Token);

        Assert.Equal(WpOutcome.Stopped, outcome);
        Assert.Equal("main", h.Git.CurrentBranch());
        Assert.True(h.Git.IsClean());
        Assert.False(File.Exists(System.IO.Path.Combine(h.Dir.Root, "partial.txt")));
        Assert.Contains("interrupted checkpoint", h.Git.FindCommit("work/WP-001")!.Subject);
    }

    // ---- Phase 5A: exact candidate identity at the mechanical gate ----------------------

    private sealed class RecordingTester(List<TesterRunContext> seen, string? forcedSha, bool forceValidShape)
        : ITesterAgent
    {
        public Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
        {
            seen.Add(ctx);
            // forcedSha null → return the requested identity; otherwise return the forced one.
            var returned = forcedSha ?? ctx.Candidate.CommitSha;
            return Task.FromResult(new TestRunResult
            {
                Passed = true,
                ExitCode = 0,
                Command = "(recording)",
                CandidateSha = forceValidShape ? returned : null,
            });
        }
    }

    [Fact]
    public async Task The_engine_passes_the_reviewed_tip_to_the_tester()
    {
        using var h = new EngineHarness(maxAttempts: 3);
        var seen = new List<TesterRunContext>();
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), h.Coder,
            new ScriptedReviewer(0), new RecordingTester(seen, forcedSha: null, forceValidShape: true),
            h.States, h.Audit);

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Done, outcome);
        var ctx = Assert.Single(seen);
        Assert.Equal("WP-001", ctx.WorkPackageId);
        Assert.Equal("work/WP-001", ctx.Candidate.WorkBranch);
        // The requested candidate is the reviewed tip: the coder commit for this attempt.
        Assert.True(Tenninety.Execution.Testing.TesterRunContext.IsFullCommitSha(ctx.Candidate.CommitSha));
        var candidateCommit = h.Git.FindCommit(ctx.Candidate.CommitSha);
        Assert.NotNull(candidateCommit);
        Assert.Contains("[attempt 1]", candidateCommit!.Subject);
        // The recorded base is main's tip BEFORE the promotion (the initial fixture commit).
        Assert.Equal("initial", h.Git.FindCommit(ctx.Candidate.MainBaseSha)!.Subject);
    }

    [Fact]
    public async Task The_engine_rejects_a_pass_bound_to_a_wrong_candidate_sha()
    {
        using var h = new EngineHarness(maxAttempts: 1, maxTotal: 1);
        var seen = new List<TesterRunContext>();
        var wrongSha = new string('f', 40);
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), h.Coder,
            new ScriptedReviewer(0), new RecordingTester(seen, forcedSha: wrongSha, forceValidShape: true),
            h.States, h.Audit);

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        // The pass is not accepted: no promotion, the mismatch is fed back as tester failure.
        Assert.Equal(WpOutcome.Blocked, outcome);
        Assert.DoesNotContain(h.Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.Contains(h.Audit.ReadTail(100), e =>
            e.Event == "TESTS_FAILED" && e.Detail.Contains("candidate identity"));
        var feedback = h.States.Load().Attempts["WP-001"].Feedback;
        Assert.Contains(feedback, f => f.Contains("candidate identity"));
        Assert.Equal("main", h.Git.CurrentBranch());
    }

    [Fact]
    public async Task The_engine_rejects_a_pass_without_any_candidate_sha()
    {
        using var h = new EngineHarness(maxAttempts: 1, maxTotal: 1);
        var seen = new List<TesterRunContext>();
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), h.Coder,
            new ScriptedReviewer(0), new RecordingTester(seen, forcedSha: null, forceValidShape: false),
            h.States, h.Audit);

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Blocked, outcome);
        Assert.DoesNotContain(h.Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.Contains(h.Audit.ReadTail(100), e =>
            e.Event == "TESTS_FAILED" && e.Detail.Contains("candidate identity"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_engine_rejects_a_reviewer_without_the_exact_candidate_identity(
        bool omitIdentity)
    {
        using var h = new EngineHarness(maxAttempts: 1, maxTotal: 1);
        var testerCalls = new List<TesterRunContext>();
        var reviewer = new IdentityReviewer(omitIdentity ? null : new string('f', 40));
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), h.Coder, reviewer,
            new RecordingTester(testerCalls, forcedSha: null, forceValidShape: true),
            h.States, h.Audit);

        var outcome = await engine.ExecuteWpAsync(
            h.Plan.WorkPackages[0], h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Blocked, outcome);
        Assert.Empty(testerCalls);
        Assert.DoesNotContain(h.Audit.ReadTail(100), e => e.Event == "WP_PROMOTED");
        Assert.Contains(h.Audit.ReadTail(100), e =>
            e.Event == "REVIEW_FAILED" && e.Detail.Contains("candidate identity"));
    }

    [Fact]
    public async Task Coder_infrastructure_failure_does_not_consume_a_candidate_attempt()
    {
        using var h = new EngineHarness(maxAttempts: 1, maxTotal: 1);
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), new InfrastructureCoder(),
            new ScriptedReviewer(0), new ScriptedTester(0), h.States, h.Audit);

        await Assert.ThrowsAsync<CoderInfrastructureException>(() =>
            engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, CancellationToken.None));

        var attempt = h.States.Load().Attempts["WP-001"];
        Assert.Equal(0, attempt.Count);
        Assert.Equal(0, attempt.Total);
        Assert.Equal(TenNinety.WpStatus.Pending, h.Plan.WorkPackages[0].Status);
    }

    [Fact]
    public async Task Docker_reviewer_cancellation_never_checkpoints_authoritative_dirt()
    {
        using var h = new EngineHarness();
        h.Config.ProviderMode = "aider";
        h.Config.Sandbox.Mode = "docker";
        using var cts = new CancellationTokenSource();
        var engine = new ExecutionEngine(
            h.Git, h.Config, new MockFrontierClient(), new SpyCoder(h.Dir.Root, h.Git),
            new CancellingMutatingReviewer(h.Dir.Root, cts), new ScriptedTester(0),
            h.States, h.Audit);

        var outcome = await engine.ExecuteWpAsync(h.Plan.WorkPackages[0], h.State, cts.Token);

        Assert.Equal(WpOutcome.Stopped, outcome);
        Assert.Equal("work/WP-001", h.Git.CurrentBranch());
        Assert.False(h.Git.IsClean());
        Assert.DoesNotContain("interrupted checkpoint",
            h.Git.FindCommit("work/WP-001")!.Subject);
    }

    private sealed class AlwaysFailReviewer : IReviewerAgent
    {
        public Task<ReviewResult> ReviewAsync(ReviewerRunContext ctx, CancellationToken ct = default) =>
            Task.FromResult(new ReviewResult
            {
                Passed = false,
                Reasons = { "hopeless" },
                CandidateSha = ctx.Candidate.CommitSha,
            });
    }

    private sealed class ThrowingReviewer : IReviewerAgent
    {
        public Task<ReviewResult> ReviewAsync(ReviewerRunContext ctx, CancellationToken ct = default) =>
            throw new InvalidOperationException("review service unavailable");
    }

    private sealed class IdentityReviewer(string? candidateSha) : IReviewerAgent
    {
        public Task<ReviewResult> ReviewAsync(
            ReviewerRunContext ctx, CancellationToken ct = default) =>
            Task.FromResult(new ReviewResult
            {
                Passed = true,
                ReviewerModel = "identity-test",
                CandidateSha = candidateSha,
            });
    }

    private sealed class InfrastructureCoder : ICoderAgent
    {
        public Task<CoderResult> ImplementAsync(
            CoderRunContext ctx, CancellationToken ct = default) =>
            throw new CoderInfrastructureException("simulated Docker startup failure");
    }

    private sealed class CancellingMutatingReviewer(
        string repoPath, CancellationTokenSource cancellation) : IReviewerAgent
    {
        public Task<ReviewResult> ReviewAsync(
            ReviewerRunContext ctx, CancellationToken ct = default)
        {
            File.WriteAllText(System.IO.Path.Combine(repoPath, "reviewer-host-dirt.txt"), "dirt");
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
    }

    private sealed class MutatingPassingTester(string repoPath) : ITesterAgent
    {
        public Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
        {
            File.WriteAllText(System.IO.Path.Combine(repoPath, "post-review.txt"), "unreviewed");
            return Task.FromResult(new TestRunResult
            {
                Passed = true,
                ExitCode = 0,
                CandidateSha = ctx.Candidate.CommitSha,
            });
        }
    }

    private sealed class BlockingPartialCoder(string repoPath) : ICoderAgent
    {
        public async Task<CoderResult> ImplementAsync(CoderRunContext ctx, CancellationToken ct = default)
        {
            File.WriteAllText(System.IO.Path.Combine(repoPath, "partial.txt"), "partial");
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
        var engine = h.CreateEngine(tester: new RequiresFileTester("main-after-pause.txt", h.Dir.Root));

        var outcome = await engine.ExecuteWpAsync(wp, h.State, CancellationToken.None);

        Assert.Equal(WpOutcome.Done, outcome);
    }

    private sealed class RequiresFileTester(string fileName, string repoPath) : ITesterAgent
    {
        public Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
        {
            var exists = File.Exists(System.IO.Path.Combine(repoPath, fileName));
            return Task.FromResult(new TestRunResult
            {
                Passed = exists,
                ExitCode = exists ? 0 : 1,
                OutputTail = exists ? "present" : "required main file missing",
                CandidateSha = ctx.Candidate.CommitSha,
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
