using Tenninety.Core.Models;
using Tenninety.Core;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Git;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// SandboxTesterGate integration tests over REAL temporary Git fixture repositories, the REAL
/// CandidateWorkspaceFactory/preflight machinery, and fake runtime/session/transport
/// dependencies that record actual requested specs, commands, lifecycle calls and ordering.
/// </summary>
public class SandboxTesterGateTests : IDisposable
{
    private static readonly string TesterImageId = "sha256:" + new string('c', 64);
    private static readonly string CoderImageId = "sha256:" + new string('a', 64);
    private static readonly string ReviewerImageId = "sha256:" + new string('b', 64);

    public TempDir RepoDir { get; } = new();
    public TempDir ManagedRoot { get; } = new();
    public GitService Git { get; }
    public TenNinetyConfig Config { get; }
    public PreflightFakeTransport FakeTransport { get; }
    public RecordingRuntime Runtime { get; }
    public readonly List<string> Lifecycle = new();

    /// <summary>The exact candidate the fixture repo is on when the gate is invoked.</summary>
    public string CandidateSha { get; private set; } = "";

    public SandboxTesterGateTests()
    {
        Git = new GitService(RepoDir.Root);
        Git.Init();
        File.WriteAllText(RepoDir.Path(".gitignore"), ".tenninety/\n");
        Directory.CreateDirectory(RepoDir.Path(".tenninety"));
        File.WriteAllText(RepoDir.Path("README.md"), "demo\n");
        // A real test project inside the candidate commit so discovery succeeds by default.
        File.WriteAllText(RepoDir.Path("tests.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
        Git.CommitAll("initial candidate");
        CandidateSha = Git.HeadSha();

        Config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            BuildCommand = "dotnet build",
            TestCommand = "dotnet test",
            Sandbox = new SandboxConfig
            {
                WorkspaceRoot = ManagedRoot.Root,
                Roles = new SandboxRolesConfig
                {
                    Coder = new CoderSandboxRoleConfig { Image = CoderImageId },
                    Reviewer = new ReviewerSandboxRoleConfig { Image = ReviewerImageId },
                    Tester = new TesterSandboxRoleConfig { Image = TesterImageId },
                },
            },
        };

        FakeTransport = new PreflightFakeTransport(Config.Sandbox);
        Runtime = new RecordingRuntime();
    }

    public void Dispose()
    {
        RepoDir.Dispose();
        ManagedRoot.Dispose();
    }

    private TesterRunContext MakeContext(int attempt = 1) => new()
    {
        Candidate = new CandidateRevision("main", CandidateSha, CandidateSha),
        WorkPackageId = "WP-001",
        Attempt = attempt,
    };

    private SandboxTesterGate MakeGate(
        Action<string>? lifecycle = null,
        Func<string, Task>? deleteOverride = null,
        TenNinetyConfig? config = null) =>
        new(Git, config ?? Config, log: null,
            transportFactory: () => new ForwardingTransport(FakeTransport, lifecycle),
            runtimeFactory: (_, _) => Runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, (config ?? Config).Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: async path =>
            {
                lifecycle?.Invoke("delete:" + System.IO.Path.GetFileName(path));
                if (deleteOverride is { } del) await del(path);
                else SandboxTesterGate.DeleteAttemptDirectory(
                    path, (config ?? Config).Sandbox.WorkspaceRoot!);
            });

    private void ScriptSuccessfulRun(
        Action<RecordingSandboxSession, string?>? configure = null,
        Action<string>? eventSink = null)
    {
        Runtime.SessionFactory = spec =>
        {
            var s = new RecordingSandboxSession
            {
                SourcePath = spec.HostWorkspacePath?.Value,
                EventSink = eventSink,
            };
            configure?.Invoke(s, s.SourcePath);
            return s;
        };
    }

    // ---- 1/2/6. exact candidate materialization, contents, identity ----------------------

    [Fact]
    public async Task The_gate_materializes_the_exact_candidate_and_binds_its_identity()
    {
        var gate = MakeGate();
        var result = await gate.RunTestsAsync(MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        var spec = Assert.IsType<SandboxSpec>(Runtime.LastSpec);
        Assert.Equal(CandidateSha, spec.CandidateSha);
        Assert.Equal(CandidateSha, spec.Labels["tenninety.candidate"]);
        Assert.Equal(CandidateSha, result.CandidateSha);

        // The session ran against the materialized source tree containing exactly the
        // candidate's tracked files (discovery used the candidate contents).
        var session = Runtime.LastSession!;
        Assert.NotNull(session.SourcePath);
        Assert.NotEqual(RepoDir.Root, session.SourcePath);
        Assert.StartsWith(ManagedRoot.Root, session.SourcePath);
        Assert.EndsWith("source", session.SourcePath);
    }

    [Fact]
    public async Task Discovery_and_commands_see_the_candidate_contents_only()
    {
        string[]? seenDuringRun = null;
        var gate = MakeGate();
        ScriptSuccessfulRun((session, source) =>
        {
            session.OnRun = _ =>
            {
                // The disposable tree holds exactly the candidate's tracked files plus the
                // disposable .git; nothing from later authoritative state can appear here.
                seenDuringRun = Directory.GetFiles(source!, "*", SearchOption.TopDirectoryOnly)
                    .Select(p => System.IO.Path.GetFileName(p))
                    .OrderBy(n => n)
                    .ToArray();
            };
        });

        // Controlled later authoritative-tree change, introduced at the session-creation
        // seam: the candidate was ALREADY materialized when this commit happens.
        Runtime.SessionFactoryWrapper = (inner, spec) =>
        {
            File.WriteAllText(RepoDir.Path("later.txt"), "authoritative drift");
            Git.CommitAll("later authoritative change");
            return inner(spec);
        };

        // The gate must reject the authoritative-state drift instead of returning a verdict.
        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("host state", ex.Message);
        Assert.NotNull(seenDuringRun);
        Assert.Contains("tests.csproj", seenDuringRun);
        Assert.Contains("README.md", seenDuringRun);
        Assert.DoesNotContain("later.txt", seenDuringRun); // absent from the materialized candidate
        // No reset/repair happened: the drift is refused, not undone.
        Assert.NotEqual(CandidateSha, Git.HeadSha());
        Assert.True(Git.IsClean());
    }

    // ---- 3. fresh source path per invocation -----------------------------------------------

    [Fact]
    public async Task Every_invocation_materializes_a_fresh_source_path()
    {
        var gate = MakeGate();

        await gate.RunTestsAsync(MakeContext());
        var first = ((SandboxSpec)Runtime.LastSpec!).HostWorkspacePath!.Value;

        ScriptSuccessfulRun();
        await gate.RunTestsAsync(MakeContext());
        var second = ((SandboxSpec)Runtime.LastSpec!).HostWorkspacePath!.Value;

        Assert.NotEqual(first, second);
        Assert.False(Directory.Exists(first), "the first attempt was cleaned up");
    }

    // ---- 4/5/6. spec shape -------------------------------------------------------------------

    [Fact]
    public async Task The_spec_mounts_only_the_disposable_source_and_is_offline()
    {
        var gate = MakeGate();
        await gate.RunTestsAsync(MakeContext());

        var spec = (SandboxSpec)Runtime.LastSpec!;
        Assert.Equal(SandboxRole.Tester, spec.Role);
        Assert.Equal(SandboxNetworkPolicy.None, spec.Network);
        Assert.Equal(TesterImageId, spec.Image);
        var source = spec.HostWorkspacePath!.Value;
        Assert.StartsWith(ManagedRoot.Root, source);
        Assert.EndsWith("/source", source);
        Assert.NotEqual(RepoDir.Root, source);
        Assert.DoesNotContain("/.git", source);
        Assert.DoesNotContain("ingestion", source);
        // The complete management identity is present and role-consistent.
        Assert.Equal("tester", spec.Labels["tenninety.role"]);
        Assert.Equal("WP-001", spec.Labels["tenninety.wp"]);
        Assert.Equal("1", spec.Labels["tenninety.attempt"]);
        foreach (var key in SandboxSpec.RequiredLabelKeys)
            Assert.True(spec.Labels.ContainsKey(key), $"missing label {key}");
    }

    // ---- 7. preflight failure ------------------------------------------------------------------

    [Fact]
    public async Task A_preflight_failure_prevents_any_candidate_execution()
    {
        FakeTransport.MissingNetworks.Add(Config.Sandbox.ModelNetwork);
        var attempts = 0;
        Runtime.SessionFactory = _ => { attempts++; return new RecordingSandboxSession(); };

        var gate = MakeGate();
        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("preflight", ex.Message);
        Assert.Equal(0, attempts);          // no session was ever created
        Assert.Null(Runtime.LastSpec);      // no candidate spec was built
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root)); // no attempt workspace
    }

    // ---- 8. restore acceptance refusal -----------------------------------------------------------

    [Fact]
    public async Task Expired_restore_acceptance_is_refused_before_any_docker_or_workspace_work()
    {
        var config = Config;
        config.Sandbox.Roles.Tester.Restore.Enabled = true;
        config.Sandbox.Roles.Tester.Restore.NetworkName = "restricted-net";
        config.Sandbox.Roles.Tester.Restore.ProxyUrl = "http://restore-proxy:8080";
        config.Sandbox.Roles.Tester.Restore.ApprovedFeeds =
            ["https://api.nuget.org/v3/index.json"];
        config.Sandbox.Roles.Tester.Restore.Acceptance = new SandboxRestoreAcceptance
        {
            Version = SandboxRestoreAcceptance.CurrentVersion,
            Accepted = true,
            Repository = SandboxTesterGate.RepositoryIdentity(Git.RepoPath),
            Instance = "tenninety",
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            NetworkId = new string('a', 64),
            FirewallProfile = "restore-egress-v1",
            StorageQuotaId = "restore-quota-v1",
            StorageQuotaBytes = 4L * 1024 * 1024 * 1024,
            HardQuotaEnforced = true,
            OperatorAcknowledged = true,
        };
        config.Sandbox.Roles.Tester.Restore.Acceptance.FeedPolicySha256 =
            config.Sandbox.Roles.Tester.Restore.ComputeFeedPolicySha256();

        var gate = MakeGate();
        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(FakeTransport.Invocations.Count > 0, "no docker call may happen");
        Assert.Null(Runtime.LastSpec);       // no runtime work
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root)); // no attempt workspace
        Assert.Equal("main", Git.CurrentBranch()); // host untouched
        Assert.Equal(CandidateSha, Git.HeadSha());
    }

    // ---- 9. no test project ------------------------------------------------------------------------

    [Fact]
    public async Task A_candidate_without_a_test_project_fails_closed()
    {
        // Candidate commits the test project, then we advance main with an app-only commit
        // and use ITS sha as the candidate (host state still matches the context).
        File.Delete(RepoDir.Path("tests.csproj"));
        File.WriteAllText(RepoDir.Path("app.csproj"), "<Project />");
        Git.CommitAll("application-only candidate");
        CandidateSha = Git.HeadSha();

        var gate = MakeGate();
        var result = await gate.RunTestsAsync(MakeContext());

        Assert.False(result.Passed);
        Assert.Contains("no test project found", result.OutputTail);
        Assert.Null(Runtime.LastSpec); // no container work for an untestable candidate
    }

    // ---- preflight warnings ----------------------------------------------------------------------

    [Fact]
    public async Task Ready_preflight_reduced_protection_warnings_are_surfaced_before_candidate_execution()
    {
        // The daemon reports seccomp+AppArmor but no SELinux: the preflight is READY with a
        // reduced-protection warning that must reach the log/audit callback anyway — and in
        // the SAME lifecycle timeline it must be observed BEFORE the first candidate command.
        var timeline = new List<string>();
        var gate = new SandboxTesterGate(Git, Config, log: message => timeline.Add("log: " + message),
            transportFactory: () => new ForwardingTransport(FakeTransport),
            runtimeFactory: (_, _) => Runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, Config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: async path =>
            {
                SandboxTesterGate.DeleteAttemptDirectory(path, Config.Sandbox.WorkspaceRoot!);
                await Task.CompletedTask;
            });
        ScriptSuccessfulRun((session, _) => session.OnRun = _ => timeline.Add("candidate command"));

        var result = await gate.RunTestsAsync(MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        // The warning did not silently disappear behind IsReady and the log line is bounded.
        var warningIndex = timeline.FindIndex(e =>
            e.StartsWith("log: ", StringComparison.Ordinal) &&
            e.Contains("tester preflight warning", StringComparison.Ordinal) &&
            e.Contains("SELinux", StringComparison.Ordinal));
        var firstCommandIndex = timeline.IndexOf("candidate command");
        Assert.True(warningIndex >= 0, "the ready-preflight warning was never logged");
        Assert.Contains(timeline, e => e.Contains("reduced protection", StringComparison.Ordinal));
        Assert.All(timeline.Where(e => e.StartsWith("log: ", StringComparison.Ordinal)),
            e => Assert.True(e.Length <= SandboxTesterGate.MaxPublicTesterMessageChars,
                "a log line exceeded the bounded length"));
        // Ordering in the same lifecycle timeline: warning observed BEFORE the first command.
        Assert.True(firstCommandIndex >= 0, "the candidate command never ran");
        Assert.True(warningIndex < firstCommandIndex,
            $"the ready-preflight warning (index {warningIndex}) must be logged before the " +
            $"first candidate command (index {firstCommandIndex})");
    }

    // ---- 10/11/16. discard mutations; host untouched; symlinks harmless -----------------------------

    [Fact]
    public async Task Source_artifacts_and_disposable_git_mutations_are_discarded()
    {
        var outside = Directory.CreateTempSubdirectory("tenninety-gate-sentinel");
        try
        {
            File.WriteAllText(System.IO.Path.Combine(outside.FullName, "sentinel.txt"), "must survive");
            string? seenSource = null;
            var gate = MakeGate();
            ScriptSuccessfulRun((session, source) =>
            {
                seenSource = source;
                session.OnRun = _ =>
                {
                    // Simulated test artifacts, source edits, disposable .git mutation and a
                    // hostile symlink pointing OUTSIDE the disposable tree.
                    File.WriteAllText(System.IO.Path.Combine(source!, "artifact.txt"), "build junk");
                    File.WriteAllText(System.IO.Path.Combine(source!, "README.md"), "tampered");
                    File.WriteAllText(System.IO.Path.Combine(source!, ".git", "mutation"), "junk");
                    var escape = System.IO.Path.Combine(source!, "escape");
                    if (File.Exists(escape) || Directory.Exists(escape) ||
                        File.Exists(System.IO.Path.Combine(source!, "escape")))
                        File.Delete(escape);
                    Directory.CreateSymbolicLink(escape, outside.FullName);
                };
            });

            var result = await gate.RunTestsAsync(MakeContext());

            Assert.True(result.Passed, result.OutputTail);
            Assert.NotNull(seenSource);
            var attemptRoot = Directory.GetParent(seenSource!)!.FullName;
            Assert.False(Directory.Exists(attemptRoot), "the mutated attempt must be discarded");

            // The hostile symlink could not damage the outside sentinel.
            Assert.Equal("must survive",
                File.ReadAllText(System.IO.Path.Combine(outside.FullName, "sentinel.txt")));
            Assert.True(Directory.Exists(outside.FullName));

            // The authoritative checkout is untouched.
            Assert.Equal("demo\n", File.ReadAllText(RepoDir.Path("README.md")));
            Assert.Equal(CandidateSha, Git.HeadSha());
            Assert.Equal("main", Git.CurrentBranch());
            Assert.Equal(CandidateSha, Git.FindCommit(TenNinety.MainBranch)!.Sha);
            Assert.True(Git.IsClean());
            Assert.True(File.Exists(RepoDir.Path("tests.csproj")));
        }
        finally
        {
            outside.Delete(recursive: true);
        }
    }

    // ---- 12. lifecycle ordering ----------------------------------------------------------------------

    [Fact]
    public async Task Stop_and_disposal_precede_workspace_deletion()
    {
        var gate = MakeGate(lifecycle: e => Lifecycle.Add(e));
        ScriptSuccessfulRun(eventSink: e => Lifecycle.Add(e));

        var result = await gate.RunTestsAsync(MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        var stop = Lifecycle.IndexOf("stop");
        var dispose = Lifecycle.IndexOf("dispose");
        var delete = Lifecycle.FindIndex(e => e.StartsWith("delete:", StringComparison.Ordinal));
        var transportDispose = Lifecycle.IndexOf("transport-dispose");
        Assert.True(stop >= 0 && dispose >= 0 && delete >= 0 && transportDispose >= 0,
            string.Join(",", Lifecycle));
        Assert.True(stop < dispose, "stop must precede dispose");
        Assert.True(dispose < delete, "disposal (proven removal) must precede workspace deletion");
        Assert.True(delete < transportDispose, "workspace deletion precedes transport disposal");
    }

    // ---- 13. cleanup on every failure path -------------------------------------------------------------

    [Fact]
    public async Task Cleanup_runs_on_build_failure_test_failure_timeout_and_cancellation()
    {
        // build failure (ordinary candidate failure: retryable result)
        var gate = MakeGate(lifecycle: e => Lifecycle.Add(e));
        ScriptSuccessfulRun((s, _) => s.Then(RecordingSandboxSession.Fail(2)));
        var buildFail = await gate.RunTestsAsync(MakeContext());
        Assert.False(buildFail.Passed);
        Assert.Equal("dotnet build", buildFail.Command);
        Assert.Single(Runtime.LastSession!.Commands);
        Assert.Contains(Lifecycle, e => e.StartsWith("delete:", StringComparison.Ordinal));
        Lifecycle.Clear();

        // test failure (ordinary candidate failure: retryable result)
        ScriptSuccessfulRun((s, _) =>
            s.Then(RecordingSandboxSession.Ok()).Then(RecordingSandboxSession.Fail(1)));
        var testFail = await MakeGate(lifecycle: e => Lifecycle.Add(e)).RunTestsAsync(MakeContext());
        Assert.False(testFail.Passed);
        Assert.Equal("dotnet test", testFail.Command);
        Assert.Equal(2, Runtime.LastSession!.Commands.Count);
        Assert.Contains(Lifecycle, e => e.StartsWith("delete:", StringComparison.Ordinal));
        Lifecycle.Clear();

        // command timeout (operational, indeterminate-but-failed result: fail closed)
        ScriptSuccessfulRun((s, _) =>
            s.Then(RecordingSandboxSession.Ok()).Then(new SandboxCommandResult(
                0, "", "", TimedOut: true, Cancelled: false, OomKilled: false,
                OutputTruncated: false, Duration: TimeSpan.FromSeconds(30))));
        var timeout = await MakeGate(lifecycle: e => Lifecycle.Add(e)).RunTestsAsync(MakeContext());
        Assert.False(timeout.Passed);
        Assert.Contains("timed out", timeout.OutputTail);
        Assert.Contains(Lifecycle, e => e.StartsWith("delete:", StringComparison.Ordinal));
        Lifecycle.Clear();

        // cancellation propagates AFTER successful cleanup (the workspace already exists)
        using var cts = new CancellationTokenSource();
        ScriptSuccessfulRun((s, _) => s.OnRun = _ => cts.Cancel());
        var gateForCancel = MakeGate(lifecycle: e => Lifecycle.Add(e));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateForCancel.RunTestsAsync(MakeContext(), cts.Token));
        Assert.Contains(Lifecycle, e => e.StartsWith("delete:", StringComparison.Ordinal)); // cleanup ran despite cancellation
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root)); // proven cleanup removed the attempt
        Lifecycle.Clear();

        // thrown session exception is an INFRASTRUCTURE failure (no automatic retry)
        ScriptSuccessfulRun((s, _) => s.ThrowOnRun = true);
        var thrown = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeGate(lifecycle: e => Lifecycle.Add(e)).RunTestsAsync(MakeContext()));
        Assert.Contains("indeterminate", thrown.Message);
        Assert.Contains(Lifecycle, e => e.StartsWith("delete:", StringComparison.Ordinal));
    }

    // ---- 14/15. cleanup failure semantics ---------------------------------------------------------------

    [Fact]
    public async Task A_deletion_failure_is_an_infrastructure_failure_and_retains_the_workspace()
    {
        string? seenSource = null;
        var gate = MakeGate(deleteOverride: _ => throw new IOException("simulated deletion failure"));
        ScriptSuccessfulRun((session, source) => seenSource = source);

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("cleanup could not be fully proven", ex.Message);
        Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(seenSource);
        // The failed deletion RETAINS the workspace instead of pretending it was cleaned.
        Assert.True(Directory.Exists(seenSource), "the workspace must be retained after a failed deletion");
        Assert.True(File.Exists(System.IO.Path.Combine(seenSource!, "README.md")));
    }

    [Fact]
    public async Task Unproven_container_removal_is_an_infrastructure_failure_and_retains_the_workspace()
    {
        string? seenSource = null;
        var gate = MakeGate();
        ScriptSuccessfulRun((session, source) =>
        {
            seenSource = source;
            session.ThrowOnDispose = true;
        });

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(seenSource);
        var attemptRoot = Directory.GetParent(seenSource!)!.FullName;
        // The workspace is conservatively retained: a container may still be writing it, so
        // the gate must NOT have recursively deleted it.
        Assert.True(Directory.Exists(attemptRoot), "a retained workspace must not be deleted");
        Assert.True(File.Exists(System.IO.Path.Combine(seenSource!, "README.md")));
    }

    // ---- 17. two attempts never interfere -----------------------------------------------------------------

    [Fact]
    public async Task Two_simultaneously_existing_attempts_are_cleaned_independently()
    {
        // Two REAL materialized attempts coexist because their cleanup seam does not delete.
        // The gate must fail closed rather than accepting that seam as proven cleanup, and a
        // later exact deletion of one attempt must never touch the other.
        string? firstAttempt = null, secondAttempt = null;
        var gate = MakeGate(deleteOverride: _ => Task.CompletedTask);
        ScriptSuccessfulRun((_, source) => firstAttempt = source);
        await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));
        ScriptSuccessfulRun((_, source) => secondAttempt = source);
        await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.NotNull(firstAttempt);
        Assert.NotNull(secondAttempt);
        var firstRoot = Directory.GetParent(firstAttempt!)!.FullName;
        var secondRoot = Directory.GetParent(secondAttempt!)!.FullName;
        Assert.NotEqual(firstRoot, secondRoot);
        Assert.True(Directory.Exists(firstRoot) && Directory.Exists(secondRoot),
            "both attempts must still exist before the cleanup");

        SandboxTesterGate.DeleteAttemptDirectory(firstRoot, ManagedRoot.Root);

        Assert.False(Directory.Exists(firstRoot), "the deleted attempt is gone");
        Assert.True(Directory.Exists(secondRoot), "the sibling attempt is preserved");
        Assert.True(File.Exists(System.IO.Path.Combine(secondAttempt!, "README.md")));
        // The configured managed root itself survives.
        Assert.True(Directory.Exists(ManagedRoot.Root));
    }

    // ---- 18. host-state mismatch fails closed ----------------------------------------------------------------

    [Theory]
    [InlineData("branch")]
    [InlineData("head")]
    [InlineData("main")]
    [InlineData("clean")]
    public async Task Host_state_mismatch_fails_closed_without_reset_or_repair(string variant)
    {
        var context = MakeContext();
        switch (variant)
        {
            case "branch":
                Git.CreateAndCheckoutBranch("other");
                Git.CheckoutBranch(TenNinety.MainBranch);
                context = new TesterRunContext
                {
                    Candidate = new CandidateRevision("not-the-current-branch", CandidateSha, CandidateSha),
                    WorkPackageId = "WP-001",
                    Attempt = 1,
                };
                break;
            case "head":
                // A real COMMITTED HEAD change (not merely an untracked file): HEAD no longer
                // matches the requested candidate, while the tree stays clean.
                File.WriteAllText(RepoDir.Path("head-moved.txt"), "committed after recording");
                Git.CommitAll("move HEAD after the candidate was recorded");
                break;
            case "main":
                // A different main base than the recorded one.
                context = new TesterRunContext
                {
                    Candidate = new CandidateRevision("main", CandidateSha, new string('e', 40)),
                    WorkPackageId = "WP-001",
                    Attempt = 1,
                };
                break;
            case "clean":
                // HEAD/base match, dirty tree: candidate recorded before a later commit.
                File.WriteAllText(RepoDir.Path("README.md"), "changed without committing");
                break;
        }

        var gate = MakeGate();
        await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(context));

        Assert.Null(Runtime.LastSpec);       // no candidate execution
        if (variant is "branch" or "main")
        {
            Assert.Equal(CandidateSha, Git.HeadSha()); // no reset/repair happened
            Assert.Equal("main", Git.CurrentBranch());
        }
    }

    // ---- cancellation and infrastructure control flow --------------------------------------------------------

    [Fact]
    public async Task An_already_cancelled_caller_starts_no_docker_or_workspace_work()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var gate = MakeGate();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.RunTestsAsync(MakeContext(), cts.Token));

        Assert.False(FakeTransport.Invocations.Count > 0, "no docker call may happen");
        Assert.Null(Runtime.LastSpec);
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
        Assert.Equal(CandidateSha, Git.HeadSha());
    }

    [Fact]
    public async Task A_session_that_cancels_the_token_and_returns_cancelled_propagates_cancellation_after_cleanup()
    {
        // The real Docker session terminates the container and RETURNS Cancelled=true
        // without throwing; with an actually-cancelled caller token the gate must propagate
        // the cancellation after proven cleanup — never convert it into a failed verdict.
        using var cts = new CancellationTokenSource();
        var gate = MakeGate(lifecycle: e => Lifecycle.Add(e));
        ScriptSuccessfulRun((s, _) =>
        {
            s.ThrowOnCallerCancellation = false;
            s.OnRun = _ => cts.Cancel();
            s.Then(new SandboxCommandResult(
                0, "partial", "", TimedOut: false, Cancelled: true,
                OomKilled: false, OutputTruncated: false, Duration: TimeSpan.FromSeconds(2)));
        }, eventSink: e => Lifecycle.Add(e));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.RunTestsAsync(MakeContext(), cts.Token));

        // Proven cleanup despite the cancelled token: disposal and deletion both happened.
        Assert.Contains(Lifecycle, e => e == "dispose");
        Assert.Contains(Lifecycle, e => e.StartsWith("delete:", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
    }

    [Fact]
    public async Task Cancellation_plus_cleanup_failure_surfaces_retained_resource_evidence()
    {
        using var cts = new CancellationTokenSource();
        string? seenSource = null;
        var gate = MakeGate();
        ScriptSuccessfulRun((s, source) =>
        {
            seenSource = source;
            s.ThrowOnCallerCancellation = false;
            s.OnRun = _ => cts.Cancel();
            s.Then(new SandboxCommandResult(
                0, "partial", "", TimedOut: false, Cancelled: true,
                OomKilled: false, OutputTruncated: false, Duration: TimeSpan.FromSeconds(2)));
            s.ThrowOnDispose = true; // container removal cannot be proven
        });

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext(), cts.Token));

        // BOTH facts surface: the caller cancellation AND the unproven cleanup/retention.
        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cleanup could not be fully proven", ex.Message);
        Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempt-", ex.Message, StringComparison.Ordinal); // bounded basename only
        Assert.DoesNotContain(ManagedRoot.Root, ex.Message, StringComparison.Ordinal); // no raw host path
        Assert.NotNull(seenSource);
        var attemptRoot = Directory.GetParent(seenSource!)!.FullName;
        Assert.True(Directory.Exists(attemptRoot), "the workspace must be retained");
    }

    [Fact]
    public async Task A_container_creation_failure_retains_the_attempt()
    {
        var gate = MakeGate();
        Runtime.SessionFactory = _ => throw new InvalidOperationException("container start could not be proven");

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("container could not be created", ex.Message);
        // A container was attempted without a returned session: conservative retention.
        var attempts = Directory.GetDirectories(ManagedRoot.Root, "attempt-*");
        Assert.Single(attempts);
        Assert.True(Directory.Exists(System.IO.Path.Combine(attempts[0], "source")));
    }

    [Fact]
    public async Task A_stop_failure_still_attempts_disposal_and_reports_both()
    {
        var gate = MakeGate(lifecycle: e => Lifecycle.Add(e));
        ScriptSuccessfulRun((s, _) => s.ThrowOnStop = true, eventSink: e => Lifecycle.Add(e));

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("container stop", ex.Message);
        var stop = Lifecycle.IndexOf("stop");
        var dispose = Lifecycle.IndexOf("dispose");
        var delete = Lifecycle.FindIndex(e => e.StartsWith("delete:", StringComparison.Ordinal));
        Assert.True(stop >= 0 && dispose >= 0, string.Join(",", Lifecycle));
        Assert.True(stop < dispose, "the stop failure must not prevent the disposal attempt");
        Assert.True(dispose < delete, "proven removal still allows the workspace deletion");
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
    }

    // ---- owned-root cleanup semantics -------------------------------------------------------------------------

    [Fact]
    public async Task Unexpected_contents_in_an_owned_root_are_retained_and_reported()
    {
        string? seenSource = null;
        var config = Config;
        config.Sandbox.WorkspaceRoot = null;
        string? ownedRoot = null;
        var gate = new SandboxTesterGate(Git, config, log: null,
            transportFactory: () => new ForwardingTransport(FakeTransport),
            runtimeFactory: (_, root) => { ownedRoot = root; return Runtime; },
            preflightFactory: (cli, root) => new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: null);
        ScriptSuccessfulRun((session, source) =>
        {
            seenSource = source;
            session.OnRun = _ =>
            {
                // Drop a file DIRECTLY into the owned managed root (outside the attempt) so
                // the root is not empty at cleanup time.
                var attemptRoot = Directory.GetParent(source!)!.FullName;
                var root = Directory.GetParent(attemptRoot)!.FullName;
                File.WriteAllText(System.IO.Path.Combine(root, "stray.txt"), "unexpected");
            };
        });

        try
        {
            var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
                () => gate.RunTestsAsync(MakeContext()));

            // The attempt workspace itself was deleted, but the non-empty owned root is
            // retained and reported by its bounded generated basename (never a host path).
            Assert.Contains("owned managed root", ex.Message);
            Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ownedRoot);
            Assert.True(Directory.Exists(ownedRoot), "the non-empty owned root must be retained");
            Assert.True(File.Exists(System.IO.Path.Combine(ownedRoot!, "stray.txt")));
            var attemptRootPath = Directory.GetParent(seenSource!)!.FullName;
            Assert.False(Directory.Exists(attemptRootPath), "the attempt itself is still discarded");
        }
        finally
        {
            // Fixture cleanup of the test-owned retained resource (production retention
            // itself stays intact; this only reclaims the system-temp test fixture).
            if (ownedRoot is not null && Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task An_owned_root_with_a_failed_attempt_deletion_is_never_recursively_deleted()
    {
        var config = Config;
        config.Sandbox.WorkspaceRoot = null;
        string? ownedRoot = null;
        string? seenSource = null;
        var gate = new SandboxTesterGate(Git, config, log: null,
            transportFactory: () => new ForwardingTransport(FakeTransport),
            runtimeFactory: (_, root) => { ownedRoot = root; return Runtime; },
            preflightFactory: (cli, root) => new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: _ => Task.FromException(new IOException("attempt deletion failed")));
        ScriptSuccessfulRun((_, source) => seenSource = source);

        try
        {
            var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
                () => gate.RunTestsAsync(MakeContext()));

            Assert.Contains("cleanup could not be fully proven", ex.Message);
            Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ownedRoot);
            // NO recursive parent fallback: the attempt AND its owned root both survive.
            Assert.True(Directory.Exists(ownedRoot), "the owned root must be retained");
            Assert.True(Directory.Exists(seenSource), "the attempt workspace must be retained");
            Assert.True(File.Exists(System.IO.Path.Combine(seenSource!, "README.md")));
        }
        finally
        {
            // Fixture cleanup of the test-owned retained resources after the assertions.
            if (ownedRoot is not null && Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_root_or_ancestor_replaced_by_a_symlink_refuses_deletion_and_preserves_the_sentinel()
    {
        // Fixture: an intermediate real directory between the system temp root and the
        // configured managed root, holding an external sentinel file.
        var outer = Directory.CreateTempSubdirectory("tenninety-gate-outer-");
        try
        {
            var rootPath = System.IO.Path.Combine(outer.FullName, "root");
            Directory.CreateDirectory(rootPath);
            var sentinel = System.IO.Path.Combine(outer.FullName, "sentinel.txt");
            File.WriteAllText(sentinel, "must survive");
            var config = Config;
            config.Sandbox.WorkspaceRoot = rootPath;

            string? seenSource = null;
            var gate = new SandboxTesterGate(Git, config, log: null,
                transportFactory: () => new ForwardingTransport(FakeTransport),
                runtimeFactory: (_, _) => Runtime,
                preflightFactory: (cli, root) => new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root),
                deleteWorkspaceOverride: async path =>
                {
                    // Replace the ROOT's parent (an ancestor of the managed root) with a
                    // symlink BEFORE the destructive cleanup runs.
                    var real = outer.FullName + "-real";
                    Directory.Move(outer.FullName, real);
                    Directory.CreateSymbolicLink(outer.FullName, real);
                    SandboxTesterGate.DeleteAttemptDirectory(path, config.Sandbox.WorkspaceRoot!);
                    await Task.CompletedTask;
                });
            ScriptSuccessfulRun((_, source) => seenSource = source);

            var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
                () => gate.RunTestsAsync(MakeContext()));

            Assert.Contains("cleanup could not be fully proven", ex.Message);
            Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
            // The external sentinel and the whole tree behind the redirect survive untouched.
            Assert.True(File.Exists(sentinel));
            Assert.Equal("must survive", File.ReadAllText(sentinel));
            Assert.NotNull(seenSource);
            Assert.True(Directory.Exists(seenSource), "the attempt workspace is preserved");
        }
        finally
        {
            var real = outer.FullName + "-real";
            if (Directory.Exists(real)) Directory.Delete(real, recursive: true);
            if (Directory.Exists(outer.FullName))
            {
                var link = new DirectoryInfo(outer.FullName);
                if (link.LinkTarget is not null) Directory.Delete(outer.FullName);
                else outer.Delete(recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_root_replaced_by_a_symlink_is_refused_by_the_deletion_helper()
    {
        var root = Directory.CreateTempSubdirectory("tenninety-gate-rootswap-");
        try
        {
            var attempt = System.IO.Path.Combine(root.FullName, "attempt-abc");
            Directory.CreateDirectory(attempt);
            File.WriteAllText(System.IO.Path.Combine(attempt, "keep.txt"), "x");

            // Replace the managed root with a symlink pointing at a moved copy.
            var real = root.FullName + "-real";
            Directory.Move(root.FullName, real);
            Directory.CreateSymbolicLink(root.FullName, real);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                SandboxTesterGate.DeleteAttemptDirectory(attempt, root.FullName));
            Assert.Contains("real directories", ex.Message); // the redirected chain is refused
            // The external sentinel (the moved tree) is intact.
            Assert.True(File.Exists(System.IO.Path.Combine(real, "attempt-abc", "keep.txt")));
        }
        finally
        {
            var real = root.FullName + "-real";
            if (Directory.Exists(real)) Directory.Delete(real, recursive: true);
            if (Directory.Exists(root.FullName)) Directory.Delete(root.FullName);
        }
    }

    // ---- owned-root initialization-failure semantics ---------------------------------------------------

    [Fact]
    public async Task An_owned_root_initialization_failure_after_creation_is_safely_removed_when_owned_and_empty()
    {
        // Deterministic failure AFTER the owned root was created and its initialization
        // (chmod, validation) COMPLETED — the runtime factory fails at the next step. This is
        // a post-initialization failure (the seam test below covers failures DURING root
        // initialization). The proven-owned EMPTY root is then safely removed by cleanup.
        var config = Config;
        config.Sandbox.WorkspaceRoot = null;
        string? ownedRoot = null;
        var gate = new SandboxTesterGate(Git, config, log: null,
            transportFactory: () => new ForwardingTransport(FakeTransport),
            runtimeFactory: (_, root) =>
            {
                ownedRoot = root;
                throw new InvalidOperationException(
                    "deterministic runtime-factory failure after root preparation");
            },
            preflightFactory: (cli, root) => new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: null);
        ScriptSuccessfulRun();

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        Assert.Contains("the tester run failed before it could produce a verdict", ex.Message);
        // The empty owned root was safely removed: no retained resource, cleanup proven.
        Assert.NotNull(ownedRoot);
        Assert.False(Directory.Exists(ownedRoot), "the proven-owned EMPTY root is safely removed");
    }

    [Fact]
    public async Task A_runtime_factory_failure_with_a_stray_entry_is_retained_and_reported()
    {
        // The same post-initialization failure, but a stray entry appears inside the owned
        // root before cleanup: the root is then NOT empty and is explicitly identified as
        // RETAINED (never recursively deleted), with the bounded basename reported.
        var config = Config;
        config.Sandbox.WorkspaceRoot = null;
        string? ownedRoot = null;
        var gate = new SandboxTesterGate(Git, config, log: null,
            transportFactory: () => new ForwardingTransport(FakeTransport),
            runtimeFactory: (_, root) =>
            {
                ownedRoot = root;
                // Stray content plus the deterministic post-initialization failure.
                File.WriteAllText(System.IO.Path.Combine(root, "stray.txt"), "unexpected");
                throw new InvalidOperationException(
                    "deterministic runtime-factory failure after root preparation");
            },
            preflightFactory: (cli, root) => new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: null);
        ScriptSuccessfulRun();

        try
        {
            var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
                () => gate.RunTestsAsync(MakeContext()));

            // Unproven cleanup surfaces first, carrying BOTH the primary stage/category and
            // the explicitly identified retained owned root (bounded basename, no host path).
            Assert.Contains("cleanup could not be fully proven", ex.Message);
            Assert.Contains("owned managed root", ex.Message);
            Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("tenninety-tester-root-", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetTempPath(), ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ownedRoot);
            Assert.True(Directory.Exists(ownedRoot), "the non-empty owned root is retained");
            Assert.True(File.Exists(System.IO.Path.Combine(ownedRoot!, "stray.txt")));
        }
        finally
        {
            // Fixture cleanup of the test-owned retained resource after the assertions.
            if (ownedRoot is not null && Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_failure_during_owned_root_initialization_is_still_owned_and_removed_when_empty()
    {
        // TRUE root-initialization failure: the injected hook throws AFTER the owned
        // directory exists and ownership is recorded, but BEFORE the remaining initialization
        // (owner-only file mode, validation) completes and BEFORE any Docker dependency or
        // preflight is invoked. The hook receives the EXACT newly created owned root path —
        // ownership is never inferred from timestamps, directory scans or prefix matches.
        // The cleanup must treat the directory as proven-owned: an EMPTY owned root is
        // safely removed.
        var config = Config;
        config.Sandbox.WorkspaceRoot = null;
        string? ownedRoot = null;
        bool transportCreated = false;
        bool preflightCreated = false;
        // An unrelated directory that happens to share the gate's root-name prefix, created
        // and owned by THIS fixture alone: neither the gate nor its cleanup may touch it.
        var unrelated = Directory.CreateTempSubdirectory("tenninety-tester-root-unrelated-");
        File.WriteAllText(System.IO.Path.Combine(unrelated.FullName, "unrelated.txt"), "other fixture");
        var gate = new SandboxTesterGate(Git, config, log: null,
            transportFactory: () => { transportCreated = true; return new ForwardingTransport(FakeTransport); },
            runtimeFactory: (_, root) => { ownedRoot = root; return Runtime; },
            preflightFactory: (cli, root) => { preflightCreated = true; return new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root); },
            deleteWorkspaceOverride: null)
        {
            OwnedRootInitializationHook = path =>
            {
                ownedRoot = path;
                throw new InvalidOperationException("deterministic owned-root initialization failure");
            },
        };

        try
        {
            var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
                () => gate.RunTestsAsync(MakeContext()));

            // The failure happened before root preparation completed: no transport or
            // preflight was EVER initialized and no Docker call or session work happened.
            Assert.False(transportCreated, "the transport must not be created before the root");
            Assert.False(preflightCreated, "the preflight must not be created before the root");
            Assert.Empty(FakeTransport.Invocations);
            Assert.Null(Runtime.LastSpec);
            // The failure is reported; the proven-owned EMPTY directory is safely removed.
            Assert.Contains("the tester run failed before it could produce a verdict", ex.Message);
            Assert.NotNull(ownedRoot);
            Assert.False(Directory.Exists(ownedRoot),
                "the proven-owned EMPTY root is safely removed by cleanup");
            // The unrelated matching-prefix directory was never touched.
            Assert.True(Directory.Exists(unrelated.FullName),
                "an unrelated matching-prefix directory must remain untouched");
            Assert.True(File.Exists(System.IO.Path.Combine(unrelated.FullName, "unrelated.txt")));
        }
        finally
        {
            // Fixture cleanup touches ONLY resources this fixture explicitly created.
            if (ownedRoot is not null && Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
            unrelated.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_failure_during_owned_root_initialization_with_unexpected_contents_is_retained_and_reported()
    {
        // The same initialization-point failure, but the hook drops unexpected contents into
        // the EXACT owned root path it received (never a scanned/timestamped guess) before
        // initialization completes: the cleanup must PRESERVE it and report its generated
        // basename (never a host path), instead of deleting it or pretending the cleanup
        // succeeded.
        var config = Config;
        config.Sandbox.WorkspaceRoot = null;
        string? ownedRoot = null;
        bool transportCreated = false;
        bool preflightCreated = false;
        // An unrelated directory that happens to share the gate's root-name prefix, created
        // and owned by THIS fixture alone.
        var unrelated = Directory.CreateTempSubdirectory("tenninety-tester-root-unrelated-");
        File.WriteAllText(System.IO.Path.Combine(unrelated.FullName, "unrelated.txt"), "other fixture");
        var gate = new SandboxTesterGate(Git, config, log: null,
            transportFactory: () => { transportCreated = true; return new ForwardingTransport(FakeTransport); },
            runtimeFactory: (_, _) => Runtime,
            preflightFactory: (cli, root) => { preflightCreated = true; return new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root); },
            deleteWorkspaceOverride: null)
        {
            OwnedRootInitializationHook = path =>
            {
                ownedRoot = path;
                File.WriteAllText(System.IO.Path.Combine(path, "stray.txt"), "unexpected");
                throw new InvalidOperationException("deterministic owned-root initialization failure");
            },
        };

        try
        {
            var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
                () => gate.RunTestsAsync(MakeContext()));

            // The failure happened before any Docker dependency or session work was invoked.
            Assert.False(transportCreated, "the transport must not be created before the root");
            Assert.False(preflightCreated, "the preflight must not be created before the root");
            Assert.Empty(FakeTransport.Invocations);

            // Unproven cleanup: the non-empty owned root is preserved and identified by its
            // generated basename (no host path is published). The reported basename is the
            // EXACT hook-captured root's basename.
            Assert.Contains("cleanup could not be fully proven", ex.Message);
            Assert.Contains("owned managed root", ex.Message);
            Assert.Contains("tenninety-tester-root-", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetTempPath(), ex.Message, StringComparison.Ordinal);
            Assert.NotNull(ownedRoot);
            var ownedBasename = System.IO.Path.GetFileName(ownedRoot!.TrimEnd('/'));
            var match = System.Text.RegularExpressions.Regex.Match(
                ex.Message, "tenninety-tester-root-[0-9A-Za-z-]+");
            Assert.True(match.Success, "the retained owned-root basename must be reported");
            Assert.Equal(ownedBasename, match.Value);
            Assert.True(Directory.Exists(ownedRoot), "the non-empty owned root is preserved");
            Assert.True(File.Exists(System.IO.Path.Combine(ownedRoot, "stray.txt")));
            // The unrelated matching-prefix directory was never touched.
            Assert.True(Directory.Exists(unrelated.FullName),
                "an unrelated matching-prefix directory must remain untouched");
            Assert.True(File.Exists(System.IO.Path.Combine(unrelated.FullName, "unrelated.txt")));
        }
        finally
        {
            // Fixture cleanup touches ONLY resources this fixture explicitly created
            // (production retention itself stays intact during the assertions).
            if (ownedRoot is not null && Directory.Exists(ownedRoot))
                Directory.Delete(ownedRoot, recursive: true);
            unrelated.Delete(recursive: true);
        }
    }

    // ---- 19/20. root ownership ---------------------------------------------------------------------------------

    // ---- 19/20. root ownership ---------------------------------------------------------------------------------

    [Fact]
    public async Task An_unset_root_creates_an_owned_private_root_and_cleans_it_up()
    {
        string? usedRoot = null;
        var config = Config;
        config.Sandbox.WorkspaceRoot = null;
        var gate = new SandboxTesterGate(Git, config, log: null,
            transportFactory: () => new ForwardingTransport(FakeTransport, Lifecycle.Add),
            runtimeFactory: (_, root) => { usedRoot = root; return Runtime; },
            preflightFactory: (cli, root) => new DockerSandboxPreflight(cli, config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: null);
        ScriptSuccessfulRun();

        var result = await gate.RunTestsAsync(MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        Assert.NotNull(usedRoot);
        Assert.NotEqual(System.IO.Path.GetTempPath().TrimEnd('/'), usedRoot!.TrimEnd('/'));
        Assert.StartsWith(System.IO.Path.GetTempPath(), usedRoot);
        Assert.False(Directory.Exists(usedRoot), "the owned default root must be deleted after the run");
    }

    [Fact]
    public async Task A_configured_root_is_never_deleted_and_survives_intact()
    {
        var marker = ManagedRoot.Path("operator-marker.txt");
        File.WriteAllText(marker, "operator data");

        var gate = MakeGate();
        ScriptSuccessfulRun();
        var result = await gate.RunTestsAsync(MakeContext());

        Assert.True(result.Passed, result.OutputTail);
        Assert.True(Directory.Exists(ManagedRoot.Root));
        Assert.Equal("operator data", File.ReadAllText(marker));
        Assert.DoesNotContain(ManagedRoot.Root,
            Directory.GetFileSystemEntries(ManagedRoot.Root)
                .Where(e => System.IO.Path.GetFileName(e).StartsWith("attempt-", StringComparison.Ordinal))
                .ToList());
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    /// <summary>Records requested specs and hands back scripted sessions; tracks lifecycle.</summary>
    public sealed class RecordingRuntime : ISandboxRuntime
    {
        public SandboxSpec? LastSpec { get; private set; }
        public RecordingSandboxSession? LastSession { get; private set; }
        public Func<SandboxSpec, ISandboxSession>? SessionFactory { get; set; }

        /// <summary>Optional outer seam invoked with the normal session factory so tests can
        /// perform controlled side effects at the exact moment container creation happens
        /// (after candidate materialization).</summary>
        public Func<Func<SandboxSpec, ISandboxSession>, SandboxSpec, ISandboxSession>?
            SessionFactoryWrapper { get; set; }

        public Task<ISandboxSession> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            LastSpec = spec;
            Func<SandboxSpec, ISandboxSession> inner = s =>
                SessionFactory?.Invoke(s)
                ?? new RecordingSandboxSession { SourcePath = s.HostWorkspacePath?.Value };
            var session = SessionFactoryWrapper?.Invoke(inner, spec) ?? inner(spec);
            LastSession = session as RecordingSandboxSession ?? LastSession;
            return Task.FromResult(session);
        }
    }

    /// <summary>Disposable forwarding transport so transport ownership/disposal is observable.</summary>
    public sealed class ForwardingTransport : IDockerCliTransport, IDisposable
    {
        private readonly IDockerCliTransport _inner;
        private readonly Action<string>? _lifecycle;
        public bool Disposed { get; private set; }

        /// <summary>When true, Dispose throws the fixed simulated failure (a transport-owned
        /// disposal failure that must surface as a controlled cleanup failure, never silently
        /// disappear). Use <see cref="DisposeException"/> for genuinely hostile objects.</summary>
        public bool ThrowOnDispose { get; set; }

        /// <summary>When set, Dispose throws THIS exception object (deterministic injection of
        /// a genuinely hostile disposal failure through the ownership seam).</summary>
        public Exception? DisposeException { get; set; }

        public ForwardingTransport(IDockerCliTransport inner, Action<string>? lifecycle = null)
        {
            _inner = inner;
            _lifecycle = lifecycle;
        }

        public Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default) =>
            _inner.RunAsync(invocation, ct);

        public void Dispose()
        {
            Disposed = true;
            _lifecycle?.Invoke("transport-dispose");
            if (DisposeException is { } failure)
                throw failure;
            if (ThrowOnDispose)
                throw new InvalidOperationException("simulated: the transport could not be disposed");
        }
    }
}
