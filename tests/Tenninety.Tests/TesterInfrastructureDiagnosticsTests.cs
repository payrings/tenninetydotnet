using Tenninety.Core.Models;
using Tenninety.Core;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Git;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Controlled-infrastructure-diagnostics regressions (Phase 5A repair C). Synthetic
/// exceptions carrying an unrelated host path, a private-hostname sentinel, arbitrary
/// secret-like text, very long messages and nested inner exceptions are injected through
/// every gate seam (preflight transport, session factory, session execution, workspace
/// deletion, transport disposal, caller cancellation). The public Tester messages must
/// contain NONE of the injected data, retain the appropriate failure categories, and obey
/// the ONE complete-message bound.
///
/// NOTE: assertions deliberately never print the sentinel values on failure — sentinels are
/// only ever used with Assert.DoesNotContain and boolean category checks.
/// </summary>
public sealed class TesterInfrastructureDiagnosticsTests : IDisposable
{
    private static readonly string TesterImageId = "sha256:" + new string('c', 64);

    // Synthetic sensitive fragments that must never reach any public diagnostic.
    private const string HostPathSentinel = "/mnt/attic-vault/host-operations-backup";
    private const string HostnameSentinel = "db-primary-01.corp.internal";
    private const string SecretSentinel = "hunter2-opaque-secret-8472";

    public TempDir RepoDir { get; } = new();
    public TempDir ManagedRoot { get; } = new();
    public GitService Git { get; }
    public TenNinetyConfig Config { get; }
    public PreflightFakeTransport FakeTransport { get; }
    public SandboxTesterGateTests.RecordingRuntime Runtime { get; } = new();
    public string CandidateSha { get; private set; } = "";

    public TesterInfrastructureDiagnosticsTests()
    {
        Git = new GitService(RepoDir.Root);
        Git.Init();
        File.WriteAllText(RepoDir.Path(".gitignore"), ".tenninety/\n");
        Directory.CreateDirectory(RepoDir.Path(".tenninety"));
        File.WriteAllText(RepoDir.Path("README.md"), "demo\n");
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
                    Coder = new CoderSandboxRoleConfig { Image = "sha256:" + new string('a', 64) },
                    Reviewer = new ReviewerSandboxRoleConfig { Image = "sha256:" + new string('b', 64) },
                    Tester = new TesterSandboxRoleConfig { Image = TesterImageId },
                },
            },
        };
        FakeTransport = new PreflightFakeTransport(Config.Sandbox);
    }

    public void Dispose()
    {
        RepoDir.Dispose();
        ManagedRoot.Dispose();
    }

    private TesterRunContext MakeContext() => new()
    {
        Candidate = new CandidateRevision("main", CandidateSha, CandidateSha),
        WorkPackageId = "WP-001",
        Attempt = 1,
    };

    /// <summary>A hostile exception: unrelated host path, private hostname, secret-like text,
    /// a very long message and a nested inner exception — none of which may leak.</summary>
    private static Exception HostileException(string note)
    {
        var longNoise = new string('L', 100_000);
        return new InvalidOperationException(
            $"outer ({note}): path {HostPathSentinel}; host {HostnameSentinel}; " +
            $"key {SecretSentinel}; {longNoise}",
            new IOException($"inner exception references {HostPathSentinel}, " +
                            $"{HostnameSentinel} and {SecretSentinel}"));
    }

    private SandboxTesterGate MakeGate(
        Func<IDockerCliTransport>? transportFactory = null,
        Func<string, Task>? deleteOverride = null) =>
        new(Git, Config, log: null,
            transportFactory:
                transportFactory ?? (() => new SandboxTesterGateTests.ForwardingTransport(FakeTransport)),
            runtimeFactory: (_, _) => Runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, Config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: deleteOverride is null
                ? path =>
                {
                    SandboxTesterGate.DeleteAttemptDirectory(path, Config.Sandbox.WorkspaceRoot!);
                    return Task.CompletedTask;
                }
                : path => deleteOverride(path));

    private void ScriptSuccessfulRun(Action<RecordingSandboxSession>? configure = null)
    {
        Runtime.SessionFactory = spec =>
        {
            var s = new RecordingSandboxSession { SourcePath = spec.HostWorkspacePath?.Value };
            configure?.Invoke(s);
            return s;
        };
    }

    private void AssertControlledMessage(string message, string expectedCategory)
    {
        // Category retained.
        Assert.True(message.Contains(expectedCategory, StringComparison.Ordinal),
            "the public message did not retain the expected category");
        // The ONE complete-message bound.
        Assert.True(message.Length <= SandboxTesterGate.MaxPublicTesterMessageChars,
            $"the public message exceeded the complete-message bound: {message.Length}");
        // No injected sentinel data anywhere (no sentinel is ever printed by these asserts).
        Assert.DoesNotContain(HostPathSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain(HostnameSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('L', 200), message, StringComparison.Ordinal);
        // No managed-root host path either.
        Assert.DoesNotContain(ManagedRoot.Root, message, StringComparison.Ordinal);
    }

    // ---- creation failure -----------------------------------------------------------------

    [Fact]
    public async Task A_hostile_container_creation_failure_publishes_only_controlled_categories()
    {
        Runtime.SessionFactory = _ => throw HostileException("creation");
        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeGate().RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "container could not be created");
    }

    // ---- execution failure ----------------------------------------------------------------

    [Fact]
    public async Task A_hostile_session_execution_failure_stays_controlled_and_indeterminate()
    {
        Runtime.SessionFactory = spec =>
            new HostileSession(HostileException("execution"));
        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeGate().RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "indeterminate");
        // Proven cleanup removed the attempt despite the hostile primary failure.
        Assert.Empty(Directory.GetDirectories(ManagedRoot.Root, "attempt-*"));
    }

    // ---- cleanup deletion failure -----------------------------------------------------------

    [Fact]
    public async Task A_hostile_deletion_failure_keeps_the_retention_evidence_without_the_sentinels()
    {
        var gate = MakeGate(deleteOverride: _ =>
            Task.FromException(HostileException("deletion")));
        string? seenSource = null;
        ScriptSuccessfulRun(s => seenSource = s.SourcePath);

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "cleanup could not be fully proven");
        Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The retained workspace carries only the bounded generated basename.
        Assert.Contains("attempt-", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(seenSource);
        Assert.True(Directory.Exists(seenSource), "the workspace must be retained");
        // Fixture cleanup of the test-owned retained resource after the assertions
        // (production retention itself stays intact during the assertions).
        var attemptRoot = Directory.GetParent(seenSource!)!.FullName;
        if (Directory.Exists(attemptRoot)) Directory.Delete(attemptRoot, recursive: true);
    }

    // ---- transport disposal failure -----------------------------------------------------------

    [Fact]
    public async Task A_hostile_transport_disposal_failure_is_a_controlled_cleanup_failure()
    {
        var gate = MakeGate(transportFactory: () => new SandboxTesterGateTests.ForwardingTransport(
            FakeTransport) { ThrowOnDispose = true });
        ScriptSuccessfulRun();

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "docker transport disposal failed");
    }

    // ---- preflight infrastructure exception ------------------------------------------------------

    [Fact]
    public async Task A_hostile_preflight_transport_exception_is_reduced_to_the_preflight_category()
    {
        var gate = MakeGate(transportFactory: () => new ThrowingTransport(HostileException("preflight")));
        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "stage preflight failed");
        Assert.False(FakeTransport.Invocations.Count > 0,
            "the hostile transport threw before any other docker call could be recorded");
    }

    // ---- cancellation: safe caller-cancellation exception ------------------------------------------

    [Fact]
    public async Task A_raw_cancellation_exception_with_arbitrary_text_is_never_rethrown()
    {
        // The fake session cancels the caller token DURING the run and throws an
        // OperationCanceledException whose message carries hostile text. After proven cleanup
        // the gate must surface a SAFE controlled caller-cancellation exception, never the
        // raw underlying one.
        using var cts = new CancellationTokenSource();
        ScriptSuccessfulRun(s =>
        {
            s.OnRun = _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(
                    "simulated caller cancellation with " + HostPathSentinel + " and " +
                    HostnameSentinel + " and " + SecretSentinel);
            };
        });

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MakeGate().RunTestsAsync(MakeContext(), cts.Token));

        AssertControlledMessage(ex.Message, "cancelled");
        // The propagated exception is the gate's SAFE controlled caller-cancellation
        // exception (cancellation type, controlled message) — never the raw underlying one.
        Assert.IsType<OperationCanceledException>(ex);
        // Cleanup was proven.
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
    }

    [Fact]
    public async Task Cancellation_plus_hostile_cleanup_failure_surfaces_both_controlled_facts()
    {
        using var cts = new CancellationTokenSource();
        ScriptSuccessfulRun(s =>
        {
            s.OnRun = _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(
                    "simulated caller cancellation with " + HostPathSentinel + " and " +
                    HostnameSentinel + " and " + SecretSentinel);
            };
            s.ThrowOnDispose = true; // container removal cannot be proven
        });

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeGate().RunTestsAsync(MakeContext(), cts.Token));

        AssertControlledMessage(ex.Message, "cleanup could not be fully proven");
        Assert.Contains("cancelled", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempt-", ex.Message, StringComparison.Ordinal);
        // Fixture cleanup of the test-owned retained resource after the assertions.
        if (Runtime.LastSession?.SourcePath is { } source)
        {
            var attemptRoot = Directory.GetParent(source)!.FullName;
            if (Directory.Exists(attemptRoot)) Directory.Delete(attemptRoot, recursive: true);
        }
    }

    // ---- host-state inspection failures and long branch text ----------------------------------------

    [Fact]
    public async Task A_long_hostile_branch_mismatch_is_never_published()
    {
        // The authoritative checkout sits on a long branch whose name carries hostile text.
        // The mismatch refusal must describe the mismatch by category — the branch text is
        // unrestricted git/context data and is never echoed.
        var longBranch =
            "main-" + HostnameSentinel.Replace('.', '-') + "-" + HostPathSentinel.Trim('/').Replace('/', '-');
        Git.CreateAndCheckoutBranch(longBranch);
        // The checkout REMAINS on the hostile branch while the candidate context is recorded
        // on 'main': the mismatch is real and must be described by category only.
        var gate = MakeGate();

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(new TesterRunContext
            {
                Candidate = new CandidateRevision("main", CandidateSha, CandidateSha),
                WorkPackageId = "WP-001",
                Attempt = 1,
            }));

        AssertControlledMessage(ex.Message, "host state does not match");
        Assert.Contains("on a different branch than the branch the candidate was recorded on",
            ex.Message, StringComparison.Ordinal);
        // The hostile branch string (context or git) is never echoed in either direction.
        Assert.DoesNotContain(longBranch, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(HostnameSentinel, ex.Message, StringComparison.Ordinal);
        Assert.False(FakeTransport.Invocations.Count > 0,
            "the initial host-state check must precede any Docker work");
    }

    [Fact]
    public async Task An_initial_host_state_inspection_failure_is_reduced_to_a_controlled_category()
    {
        // The initial inspection must fail closed when git inspection itself throws (arbitrary
        // git messages with paths must never reach the public message), and no resource work
        // may happen.
        var flaky = FlakyGitService.Create(Git,
            method => method == "CurrentBranch" ? HostileException("branch inspection") : null);
        var gate = new SandboxTesterGate(flaky, Config, log: null,
            transportFactory: () => new SandboxTesterGateTests.ForwardingTransport(FakeTransport),
            runtimeFactory: (_, _) => Runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, Config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: path =>
            {
                SandboxTesterGate.DeleteAttemptDirectory(path, Config.Sandbox.WorkspaceRoot!);
                return Task.CompletedTask;
            });

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message,
            "the initial authoritative host-state inspection failed");
        Assert.False(FakeTransport.Invocations.Count > 0,
            "no Docker work may follow an initial inspection failure");
    }

    [Fact]
    public async Task A_final_host_state_recheck_inspection_failure_is_reduced_to_a_controlled_category()
    {
        // The authoritative git inspection starts throwing only DURING the run (hooked from
        // the session), so the FINAL recheck fails. After proven cleanup the gate must
        // surface the controlled recheck category — never the git exception text.
        string? seenSource = null;
        var flaky = FlakyGitService.Create(Git, method => method == "HeadSha" && seenSource is not null
            ? HostileException("head inspection")
            : null);
        ScriptSuccessfulRun(s => seenSource = s.SourcePath);
        var gate = new SandboxTesterGate(flaky, Config, log: null,
            transportFactory: () => new SandboxTesterGateTests.ForwardingTransport(FakeTransport),
            runtimeFactory: (_, _) => Runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, Config.Sandbox, root, RepoDir.Root),
            deleteWorkspaceOverride: path =>
            {
                SandboxTesterGate.DeleteAttemptDirectory(path, Config.Sandbox.WorkspaceRoot!);
                return Task.CompletedTask;
            });

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message,
            "the final authoritative host-state inspection failed");
        // The verdict is a refusal: no pass was published even though the container work
        // succeeded, and proven cleanup still removed the attempt.
        Assert.Empty(Directory.GetFileSystemEntries(ManagedRoot.Root));
    }

    [Fact]
    public async Task A_genuinely_hostile_transport_disposal_exception_is_a_controlled_cleanup_failure()
    {
        // Not the historical fixed "simulated" message: a real exception object carrying
        // unrelated host paths, a private hostname, secret-like text and a very long message.
        var gate = MakeGate(transportFactory: () => new SandboxTesterGateTests.ForwardingTransport(
            FakeTransport) { DisposeException = HostileException("disposal") });
        ScriptSuccessfulRun();

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "docker transport disposal failed");
        Assert.Contains("InvalidOperationException", ex.Message, StringComparison.Ordinal);
    }

    // ---- complete-message boundary lengths -----------------------------------------------------------

    [Fact]
    public void The_complete_message_bound_never_exceeds_4000_characters_including_the_marker()
    {
        // Genuine over-limit assembly: a message whose head carries the primary category and
        // whose tail carries the retention evidence. The bound must produce EXACTLY 4000
        // characters (marker space reserved first), preserving both ends.
        const int limit = SandboxTesterGate.MaxPublicTesterMessageChars;
        string head = "the tester cleanup could not be fully proven; primary failure: stage preflight failed";
        string tail = " retained: attempt 'attempt-0123456789abcdef' (run tester-0123456789ab)";

        foreach (var total in new[] { 3998, 3999, 4000, 4001, 4009, 4010, 5000, 100_000 })
        {
            var padding = total > head.Length + tail.Length
                ? new string('m', total - head.Length - tail.Length)
                : "";
            var assembled = padding.Length > 0 ? head + padding + tail : head + tail[..Math.Min(tail.Length, total - head.Length)];

            var bounded = SandboxTesterGate.FinalPublicBound(assembled);

            Assert.True(bounded.Length <= limit,
                $"an assembled message of {assembled.Length} characters was bounded to {bounded.Length}");
            Assert.True(bounded.Length <= SandboxTesterGate.MaxPublicTesterMessageChars,
                $"the complete message exceeded the limit: {bounded.Length}");
            if (assembled.Length > SandboxTesterGate.MaxPublicTesterMessageChars)
            {
                // Both controlled ends survive: the primary category at the head and the
                // retained-resource identifier at the tail.
                Assert.StartsWith(head, bounded, StringComparison.Ordinal);
                Assert.EndsWith(tail, bounded, StringComparison.Ordinal);
                Assert.Contains("…[bounded]", bounded, StringComparison.Ordinal);
                Assert.Equal(SandboxTesterGate.MaxPublicTesterMessageChars, bounded.Length);
            }
            else
            {
                Assert.Equal(assembled, bounded);
            }
        }
    }

    [Fact]
    public void The_boundary_is_exact_at_4000_characters_with_and_without_the_marker()
    {
        // Exactly-at-limit assemblies pass through unchanged (no marker appended).
        var exact = new string('a', SandboxTesterGate.MaxPublicTesterMessageChars);
        Assert.Equal(SandboxTesterGate.MaxPublicTesterMessageChars,
            SandboxTesterGate.FinalPublicBound(exact).Length);
        Assert.Equal(exact, SandboxTesterGate.FinalPublicBound(exact));
        // One over: the marker space is reserved so the output is EXACTLY the limit —
        // the historical implementation produced 4010 characters here. The middle is
        // elided under the marker; both ends are preserved.
        var over = exact + 'x';
        var bounded = SandboxTesterGate.FinalPublicBound(over);
        Assert.Equal(SandboxTesterGate.MaxPublicTesterMessageChars, bounded.Length);
        Assert.Contains("…[bounded]", bounded, StringComparison.Ordinal);
    }

    // ---- provenance attack: the CLR type never establishes message safety ---------------------

    [Fact]
    public async Task A_session_throwing_the_typed_exception_with_hostile_text_is_reduced_not_published()
    {
        // The injected session throws the SAME TesterInfrastructureException type through its
        // public constructor with hostile arbitrary text. The CLR type proves nothing: the
        // gate must reduce it to the controlled execution category, never publish the text.
        Runtime.SessionFactory = spec =>
            new HostileSession(new TesterInfrastructureException(
                $"hostile typed message: {HostPathSentinel}; {HostnameSentinel}; " +
                $"{SecretSentinel}; {new string('L', 100_000)}"));
        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeGate().RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "indeterminate");
        // The published instance is the Tester's own controlled composition, not the
        // injected untrusted one.
        Assert.Equal(TesterInfrastructureProvenance.Controlled, ex.Provenance);
        // Proven cleanup removed the attempt despite the hostile primary failure.
        Assert.Empty(Directory.GetDirectories(ManagedRoot.Root, "attempt-*"));
    }

    [Fact]
    public async Task A_hostile_typed_session_failure_plus_cleanup_failure_preserves_both_controlled_facts()
    {
        // The untrusted typed failure is combined with an unproven container removal: the
        // public message must carry BOTH the reduced primary category and the retention
        // evidence, and none of the hostile text.
        Runtime.SessionFactory = spec => new HostileSession(
            new TesterInfrastructureException(
                $"typed: {HostPathSentinel} {HostnameSentinel} {SecretSentinel}"))
        {
            ThrowOnDispose = true,
        };

        var ex = await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => MakeGate().RunTestsAsync(MakeContext()));

        AssertControlledMessage(ex.Message, "cleanup could not be fully proven");
        // The untrusted typed instance was reduced by the execution stage to the gate's own
        // controlled infrastructure-error composition (type name only, no hostile text).
        Assert.Contains("session infrastructure error (TesterInfrastructureException)",
            ex.Message, StringComparison.Ordinal);
        Assert.Contains("retained", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attempt-", ex.Message, StringComparison.Ordinal);
        // Fixture cleanup of the test-owned retained resource after the assertions.
        if (Runtime.LastSession?.SourcePath is { } source)
        {
            var attemptRoot = Directory.GetParent(source)!.FullName;
            if (Directory.Exists(attemptRoot)) Directory.Delete(attemptRoot, recursive: true);
        }
    }

    // ---- hostile fakes ---------------------------------------------------------------

    /// <summary>A session whose RunAsync throws the hostile synthetic exception and whose
    /// stop/dispose prove cleanup (so the run reduces to the controlled execution failure).
    /// Optionally also fails disposal to model unproven container removal.</summary>
    private sealed class HostileSession : ISandboxSession
    {
        private readonly Exception _failure;
        public HostileSession(Exception failure) => _failure = failure;
        public bool ThrowOnDispose { get; init; }
        public SandboxSessionInfo Info { get; } = new(
            new string('e', 64), SandboxRole.Tester, SandboxSessionState.Running);
        public Task<SandboxCommandResult> RunAsync(
            SandboxCommand command, CancellationToken ct = default) =>
            Task.FromException<SandboxCommandResult>(_failure);
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async ValueTask DisposeAsync()
        {
            if (ThrowOnDispose)
                throw new InvalidOperationException("simulated: container removal could not be proven");
            await ValueTask.CompletedTask;
        }
    }

    /// <summary>A transport whose RunAsync throws the hostile synthetic exception.</summary>
    private sealed class ThrowingTransport : IDockerCliTransport, IDisposable
    {
        private readonly Exception _failure;
        public ThrowingTransport(Exception failure) => _failure = failure;
        public Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default) =>
            Task.FromException<DockerCliResult>(_failure);
        public void Dispose() { }
    }

    /// <summary>A transparent IGitService proxy that can make SELECTED inspection methods
    /// throw on demand — deterministic host-state inspection failures without mutating the
    /// real repository. Every other call is delegated unchanged to the real service.
    /// (DispatchProxy requires an unsealed proxy type.)</summary>
    private class FlakyGitService : System.Reflection.DispatchProxy
    {
        private IGitService _inner = null!;
        private Func<string, Exception?>? _behavior;

        public static IGitService Create(IGitService inner, Func<string, Exception?>? behavior)
        {
            var proxy = Create<IGitService, FlakyGitService>();
            var typed = (FlakyGitService)(object)proxy;
            typed._inner = inner;
            typed._behavior = behavior;
            return (IGitService)(object)proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (_behavior?.Invoke(targetMethod?.Name ?? "") is { } failure)
                throw failure;
            return targetMethod!.Invoke(_inner, args);
        }
    }
}
