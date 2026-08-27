using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Execution.Mock;
using Tenninety.Execution.OpenAi;
using Tenninety.Core.Validation;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Regression suite for the external repository review findings:
/// restart hydration, control-channel polling, fail-closed tester, bounded diff review,
/// untrusted-plan validation, sanitiser robustness, quoted extra args.
/// </summary>
public class HardeningTests
{
    // ---------- F2 · runtime state is authoritative across restarts ----------

    [Fact]
    public void Restart_hydrates_terminal_statuses_from_state()
    {
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();

        var plan = TestPlans.Simple(); // 001 → 002 → 003
        var state = new RuntimeState { QueueStatus = { ["WP-001"] = TenNinety.WpStatus.Done } };

        var orchestrator = MakeOrchestrator(tmp, git, plan, state);

        // WP-001 must NOT be re-executed even though plan.json still says PENDING.
        Assert.Equal("WP-002", orchestrator.SelectNextReady()!.Id);
    }

    [Fact]
    public void Stale_active_status_from_a_crash_falls_back_to_pending()
    {
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();

        var plan = TestPlans.Simple();
        var state = new RuntimeState { QueueStatus = { ["WP-001"] = TenNinety.WpStatus.Active } };

        var orchestrator = MakeOrchestrator(tmp, git, plan, state);

        // A hard crash mid-job must be resumable, never permanently unschedulable.
        Assert.Equal("WP-001", orchestrator.SelectNextReady()!.Id);
    }

    [Fact]
    public async Task External_pause_file_stops_the_daemon_with_paused_exit()
    {
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();
        Directory.CreateDirectory(System.IO.Path.Combine(tmp.Root, ".tenninety"));
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, ".tenninety", ".gitignore"),
            RuntimeGitignoreMigration.Contents);
        git.CommitPaths([".tenninety/.gitignore"], "ignore runtime files");

        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        var states = new StateStore(System.IO.Path.Combine(tmp.Root, ".tenninety", "state.json"));
        foreach (var wp in plan.WorkPackages) state.QueueStatus[wp.Id] = wp.Status;
        states.Save(state);

        var orchestrator = new Orchestrator(
            git, plan, state, new TenNinetyConfig(), new MockFrontierClient(),
            states, new AuditLog(System.IO.Path.Combine(tmp.Root, ".tenninety", "audit-log.jsonl")));

        // A separate process communicates only through the marker; it does not share or mutate
        // the daemon's in-memory state object.
        ExecutionControl.SetPause(tmp.Root);

        var runTask = orchestrator.RunAsync(CancellationToken.None);
        var finished = await Task.WhenAny(runTask, Task.Delay(15_000));
        Assert.Same(runTask, finished);
        Assert.Equal(OrchestratorExit.Paused, await runTask);
        Assert.False(ExecutionControl.ReadFlags(tmp.Root).PauseRequested);
    }

    [Fact]
    public async Task Fresh_runtime_ignore_does_not_dirty_the_first_run()
    {
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();
        Directory.CreateDirectory(System.IO.Path.Combine(tmp.Root, ".tenninety"));
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, ".tenninety", ".gitignore"),
            RuntimeGitignoreMigration.Contents);
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "README.md"), "demo");
        git.CommitPaths([".tenninety/.gitignore", "README.md"], "initial");

        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        foreach (var wp in plan.WorkPackages) state.QueueStatus[wp.Id] = wp.Status;
        var states = new StateStore(System.IO.Path.Combine(tmp.Root, ".tenninety", "state.json"));
        var orchestrator = new Orchestrator(
            git, plan, state, new TenNinetyConfig(), new MockFrontierClient(), states,
            new AuditLog(System.IO.Path.Combine(tmp.Root, ".tenninety", "audit-log.jsonl")));

        var result = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(OrchestratorExit.Completed, result);
        Assert.True(git.IsClean());
    }

    [Fact]
    public async Task Older_runtime_ignore_is_migrated_and_committed_before_execution()
    {
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();
        Directory.CreateDirectory(System.IO.Path.Combine(tmp.Root, ".tenninety"));
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, ".tenninety", ".gitignore"),
            "state.json\naudit-log.jsonl\n");
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, ".tenninety", "state.json.lock"),
            "stale runtime lock");
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "README.md"), "demo");
        git.CommitPaths([".tenninety/.gitignore", "README.md"], "initial");

        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        foreach (var wp in plan.WorkPackages)
        {
            wp.Status = TenNinety.WpStatus.Done;
            state.QueueStatus[wp.Id] = TenNinety.WpStatus.Done;
        }
        var orchestrator = new Orchestrator(
            git, plan, state, new TenNinetyConfig(), new MockFrontierClient(),
            new StateStore(System.IO.Path.Combine(tmp.Root, ".tenninety", "state.json")),
            new AuditLog(System.IO.Path.Combine(tmp.Root, ".tenninety", "audit-log.jsonl")));

        var result = await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(OrchestratorExit.Completed, result);
        Assert.True(git.IsClean());
        var ignore = File.ReadAllLines(System.IO.Path.Combine(tmp.Root, ".tenninety", ".gitignore"));
        Assert.All(RuntimeGitignoreMigration.RequiredLines, line => Assert.Contains(line, ignore));
        Assert.Equal("tenninety: update runtime ignores", git.RecentCommits(1).Single().Subject);
    }

    private static Orchestrator MakeOrchestrator(
        TempDir tmp, Git.GitService git, Plan plan, RuntimeState state)
    {
        var states = new StateStore(System.IO.Path.Combine(tmp.Root, ".tenninety", "state.json"));
        var audit = new AuditLog(System.IO.Path.Combine(tmp.Root, ".tenninety", "audit-log.jsonl"));
        return new Orchestrator(git, plan, state, new TenNinetyConfig(),
            new MockFrontierClient(), states, audit);
    }

    // ---------- F4 · the reviewer sees the real bounded patch ----------

    private sealed class RecordingChat(string reply) : IChatClient
    {
        public string? LastPrompt { get; private set; }
        public Task<string> CompleteAsync(string model, string system, string user, CancellationToken ct)
        {
            LastPrompt = user;
            return Task.FromResult(reply);
        }
    }

    [Fact]
    public async Task Reviewer_prompt_contains_the_unified_patch()
    {
        var chat = new RecordingChat("{\"verdict\":\"PASS\",\"reasons\":[]}");
        var agent = new OpenAiReviewerAgent(chat, "devstral-reviewer");
        var ctx = MakeCtx();
        var ctxWithDiff = new WpContext
        {
            RepoPath = ctx.RepoPath, WorkPackage = ctx.WorkPackage,
            Attempt = 1, DiffPatch = "diff --git a/Game.cs b/Game.cs\n+new code",
        };

        var result = await agent.ReviewAsync(ctxWithDiff, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("diff --git", chat.LastPrompt);
    }

    [Fact]
    public async Task Reviewer_flags_a_missing_diff_instead_of_passing_blindly()
    {
        var chat = new RecordingChat("{\"verdict\":\"FAIL\",\"reasons\":[\"no diff\"]}");
        var agent = new OpenAiReviewerAgent(chat, "devstral-reviewer");

        var result = await agent.ReviewAsync(MakeCtx(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("(no diff available", chat.LastPrompt);
    }

    [Fact]
    public async Task Reviewer_fails_closed_without_calling_model_for_a_truncated_diff()
    {
        var chat = new RecordingChat("{\"verdict\":\"PASS\",\"reasons\":[]}");
        var agent = new OpenAiReviewerAgent(chat, "devstral-reviewer");
        var ctx = MakeCtx();

        var result = await agent.ReviewAsync(new WpContext
        {
            RepoPath = ctx.RepoPath,
            WorkPackage = ctx.WorkPackage,
            Attempt = 1,
            DiffPatch = "diff --git a/a b/a\n[diff truncated - showing head and tail]",
        });

        Assert.False(result.Passed);
        Assert.Null(chat.LastPrompt);
    }

    [Fact]
    public async Task Reviewer_rejects_pass_with_failure_reasons()
    {
        var chat = new RecordingChat("{\"verdict\":\"PASS\",\"reasons\":[\"serious defect\"]}");
        var result = await new OpenAiReviewerAgent(chat, "devstral-reviewer")
            .ReviewAsync(MakeCtx());

        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains("invalid or contradictory"));
    }

    [Fact]
    public async Task Reviewer_sanitizes_work_package_content()
    {
        const string secret = "supersecretvalue123";
        var chat = new RecordingChat("{\"verdict\":\"PASS\",\"reasons\":[]}");
        var wp = TestPlans.Wp("WP-001");
        wp.Goal = $"Use client_secret={secret}";

        await new OpenAiReviewerAgent(chat, "devstral-reviewer").ReviewAsync(new WpContext
        {
            RepoPath = "/tmp",
            WorkPackage = wp,
            Attempt = 1,
            DiffPatch = "diff --git a/a b/a\n+safe",
        });

        Assert.DoesNotContain(secret, chat.LastPrompt);
        Assert.Contains("REDACTED", chat.LastPrompt);
    }

    private static WpContext MakeCtx() => new()
    {
        RepoPath = "/tmp",
        WorkPackage = TestPlans.Wp("WP-001"),
        Attempt = 1,
    };

    // ---------- F7 · mechanical gate fails closed in live mode ----------

    [Fact]
    public async Task Live_mode_without_any_project_fails_closed()
    {
        using var tmp = new TempDir();
        var tester = new ShellTesterAgent("dotnet test", simulatedFailAttempts: 0,
            log: null, failWhenNoProject: true, buildCommand: "dotnet build");

        var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("failing closed", result.OutputTail);
    }

    [Fact]
    public async Task Live_mode_with_empty_test_command_fails_closed()
    {
        using var tmp = new TempDir();
        // A real TEST project exists, but no test command was configured.
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        var tester = new ShellTesterAgent("", simulatedFailAttempts: 0,
            log: null, failWhenNoProject: true, buildCommand: "");

        var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("test_command", result.OutputTail);
    }

    [Fact]
    public async Task Application_only_solutions_fail_closed_even_when_present()
    {
        using var tmp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "app.csproj"), "<Project/>");
        var tester = new ShellTesterAgent("dotnet test", simulatedFailAttempts: 0,
            log: null, failWhenNoProject: true, buildCommand: "");

        var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("no test project found", result.OutputTail);
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>")]
    [InlineData("<Project><!-- xunit is not actually referenced --></Project>")]
    public async Task False_test_markers_do_not_satisfy_discovery(string project)
    {
        using var tmp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "fake.csproj"), project);
        var tester = new ShellTesterAgent("true", failWhenNoProject: true, buildCommand: "");

        var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root));

        Assert.False(result.Passed);
        Assert.Contains("no test project found", result.OutputTail);
    }

    [Fact]
    public async Task Successful_command_that_reports_zero_tests_fails_closed()
    {
        using var tmp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        var tester = new ShellTesterAgent(
            "printf 'No test is available in the selected project'", failWhenNoProject: true, buildCommand: "");

        var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root));

        Assert.False(result.Passed);
        Assert.Contains("zero tests were executed", result.OutputTail);
    }

    [Theory]
    [InlineData("No test matches the given testcase filter")]
    [InlineData("Total tests: 0")]
    public async Task Other_zero_test_summaries_fail_closed(string output)
    {
        using var tmp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        var tester = new ShellTesterAgent(
            $"printf '{output}'", failWhenNoProject: true, buildCommand: "");

        var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root));

        Assert.False(result.Passed);
        Assert.Contains("zero tests were executed", result.OutputTail);
    }

    [Fact]
    public async Task Build_command_runs_before_tests_and_in_nested_projects()
    {
        using var tmp = new TempDir();
        var nested = System.IO.Path.Combine(tmp.Root, "src", "app");
        Directory.CreateDirectory(nested);
        File.WriteAllText(System.IO.Path.Combine(nested, "app.Tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");

        var tester = new ShellTesterAgent("true", simulatedFailAttempts: 0,
            log: null, failWhenNoProject: false, buildCommand: "touch build.ran");

        var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root), CancellationToken.None);

        Assert.True(result.Passed);
        // The gate executes from the workspace root – we only assert that it RAN.
        Assert.True(File.Exists(System.IO.Path.Combine(tmp.Root, "build.ran")),
            "the build gate should have executed");
    }

    [Fact]
    public async Task Test_process_does_not_inherit_host_credentials()
    {
        using var tmp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(tmp.Root, "tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        const string variable = "TENNINETY_TEST_HOST_SECRET";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "sentinel");
        try
        {
            var tester = new ShellTesterAgent(
                $"test -z \"${{{variable}:-}}\"", failWhenNoProject: true, buildCommand: "");
            var result = await tester.RunTestsAsync(MakeTesterCtx(tmp.Root));
            Assert.True(result.Passed, result.OutputTail);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void Daemon_lock_excludes_a_second_owner_and_is_reusable()
    {
        using var tmp = new TempDir();
        var git = new GitService(tmp.Root);
        git.Init();

        using (DaemonLock.Acquire(tmp.Root))
            Assert.Throws<InvalidOperationException>(() => DaemonLock.Acquire(tmp.Root));

        using var reacquired = DaemonLock.Acquire(tmp.Root);
    }

    private static WpContext MakeTesterCtx(string root) => new()
    {
        RepoPath = root,
        WorkPackage = TestPlans.Wp("WP-001"),
        Attempt = 1,
    };

    // ---------- F5c/F6 · endpoint normalisation & untrusted-plan validation ----------

    [Fact]
    public void Endpoints_keep_the_v1_prefix_when_used_as_http_base()
    {
        var factory = new AgentFactory(new TenNinetyConfig
        {
            UseLlamaSwap = true,
            LlamaSwapEndpoint = "http://localhost:8080/v1",
        });
        Assert.Equal("http://localhost:8080/v1/", factory.EndpointFor("coder"));
    }

    [Theory]
    [InlineData("WP-AUTH-01")]
    [InlineData("wp-001")]
    [InlineData("../../etc/passwd")]
    [InlineData("WP-1; rm -rf /")]
    public void Hostile_or_noncanonical_ids_are_rejected(string id)
    {
        var plan = new Plan
        {
            ProjectName = "Hostile",
            WorkPackages = { TestPlans.Wp(id) },
        };
        var result = PlanValidator.Validate(plan);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Unknown_layers_are_hard_errors_not_warnings()
    {
        var plan = new Plan { ProjectName = "X", WorkPackages = { TestPlans.Wp("WP-001", "BLOCKCHAIN") } };
        var result = PlanValidator.Validate(plan);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unknown layer"));
    }

    [Fact]
    public void Arriving_plans_must_declare_pending_but_are_never_mutated_by_validation()
    {
        var plan = TestPlans.Simple();
        plan.WorkPackages[0].Status = TenNinety.WpStatus.Done; // a model trying to pre-mark progress

        var result = PlanValidator.Validate(plan);

        Assert.True(result.IsValid); // warning only here…
        Assert.Contains(result.Warnings, w => w.Contains("must use status 'PENDING'"));
        Assert.Equal(TenNinety.WpStatus.Done, plan.WorkPackages[0].Status); // …and validation itself stays pure
    }

    [Fact]
    public void Duplicate_dependencies_are_rejected()
    {
        var plan = new Plan
        {
            ProjectName = "Dup",
            WorkPackages =
            {
                TestPlans.Wp("WP-001"),
                TestPlans.Wp("WP-002", "DOMAIN", "WP-001", "WP-001"),
            },
        };
        Assert.False(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Oversized_packages_are_rejected()
    {
        var plan = TestPlans.Simple();
        plan.WorkPackages[0].Directives.Add(new string('x', 501));
        Assert.False(PlanValidator.Validate(plan).IsValid);
    }
}

public class SanitizerAdversarialTests
{
    [Theory]
    [InlineData("apiKey: supersecretvalue123")]
    [InlineData("api_key=\"supersecretvalue123\"")]
    [InlineData("password=hunter2hunter2,remember=true")]
    [InlineData("token\t:\tabcdefghijklmnop")]
    public void Inline_secrets_are_redacted_without_breaking_the_line(string line)
    {
        var output = Core.Security.Sanitizer.SanitizeText(line);
        Assert.DoesNotContain(line.Trim(), output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[REDACTED]", output);
    }

    [Fact]
    public void Short_or_trailing_values_never_throw()
    {
        // Previously the slice arithmetic could misbehave; these must all survive cleanly.
        foreach (var line in new[] { "token:", "secret=", "password=", "key token", "x" })
            Core.Security.Sanitizer.SanitizeText(line);
    }
}

public class QuotedExtraArgsTests
{
    [Fact]
    public void Double_quoted_values_survive_the_split()
    {
        var args = Execution.CliCoderAgentBase.SplitExtraArgs("--model \"my model v2\" --verbose");
        Assert.Equal(new[] { "--model", "my model v2", "--verbose" }, args);
    }

    [Fact]
    public void Empty_input_yields_no_arguments()
    {
        Assert.Empty(Execution.CliCoderAgentBase.SplitExtraArgs(""));
        Assert.Empty(Execution.CliCoderAgentBase.SplitExtraArgs("   "));
    }
}
