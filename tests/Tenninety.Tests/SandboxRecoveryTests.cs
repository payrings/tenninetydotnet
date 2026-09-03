using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Execution.Sandbox;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Tests;

public sealed class SandboxRecoveryTests
{
    private static readonly string ContainerId = new('a', 64);

    [Fact]
    public async Task Recovery_removes_only_scoped_containers_and_journaled_workspaces()
    {
        using var repo = Repository();
        using var managed = new TempDir();
        var tracked = Path.Combine(managed.Root, "attempt-owned");
        var unrelated = Path.Combine(managed.Root, "attempt-unrelated");
        Directory.CreateDirectory(tracked);
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "unrelated");
        var journal = Journal(repo.Root, managed.Root, tracked, ContainerId);
        var transport = new RecoveryTransport(ContainerId, failCleanup: false);

        var result = await new SandboxRecoveryService(
            new GitService(repo.Root), LiveConfig(), () => transport)
            .RecoverAsync(CancellationToken.None);

        Assert.Equal("recovered", result.Status);
        Assert.Equal(1, result.ContainersFound);
        Assert.Equal(1, result.ContainersRemoved);
        Assert.Equal(1, result.WorkspacesFound);
        Assert.Equal(1, result.WorkspacesRemoved);
        Assert.False(Directory.Exists(tracked));
        Assert.True(File.Exists(Path.Combine(unrelated, "keep.txt")));
        Assert.Empty(journal.ReadAll());
        var list = Assert.Single(transport.Invocations, invocation => invocation.Arguments[0] == "ps");
        Assert.Contains("label=tenninety.instance=tenninety", list.Arguments);
        Assert.Contains(
            "label=tenninety.repository=" + SandboxPolicy.RepositoryIdentity(repo.Root),
            list.Arguments);
    }

    [Fact]
    public async Task Failed_container_cleanup_quarantines_workspace_and_retry_can_finish()
    {
        using var repo = Repository();
        using var managed = new TempDir();
        var tracked = Path.Combine(managed.Root, "attempt-retry");
        Directory.CreateDirectory(tracked);
        var journal = Journal(repo.Root, managed.Root, tracked, ContainerId);

        var failed = await new SandboxRecoveryService(
            new GitService(repo.Root), LiveConfig(),
            () => new RecoveryTransport(ContainerId, failCleanup: true))
            .RecoverAsync(CancellationToken.None);

        Assert.Equal("quarantined", failed.Status);
        Assert.True(Directory.Exists(tracked));
        Assert.Single(journal.ReadAll());

        var retried = await new SandboxRecoveryService(
            new GitService(repo.Root), LiveConfig(),
            () => new RecoveryTransport(ContainerId, failCleanup: false))
            .RecoverAsync(CancellationToken.None);

        Assert.Equal("recovered", retried.Status);
        Assert.False(Directory.Exists(tracked));
        Assert.Empty(journal.ReadAll());
    }

    [Fact]
    public async Task Mock_mode_never_constructs_a_docker_transport()
    {
        using var repo = Repository();
        var result = await new SandboxRecoveryService(
            new GitService(repo.Root), new TenNinetyConfig(),
            () => throw new InvalidOperationException("must not be called"))
            .RecoverAsync(CancellationToken.None);

        Assert.Equal("not-required", result.Status);
    }

    [Fact]
    public async Task Orchestrator_persists_quarantine_and_refuses_to_schedule()
    {
        using var repo = Repository(includeAllRuntimeIgnores: true);
        var git = new GitService(repo.Root);
        var plan = TestPlans.Simple();
        var state = new RuntimeState();
        foreach (var wp in plan.WorkPackages) state.QueueStatus[wp.Id] = wp.Status;
        var stateStore = new StateStore(
            Path.Combine(repo.Root, ".tenninety", "state.json"));
        var orchestrator = new Orchestrator(
            git, plan, state, new TenNinetyConfig(), new MockFrontierClient(),
            stateStore,
            new AuditLog(Path.Combine(repo.Root, ".tenninety", "audit-log.jsonl")))
        {
            RecoveryOverride = _ => Task.FromResult(new SandboxRecoveryInfo
            {
                Status = "quarantined",
                LastRunUtc = DateTimeOffset.UtcNow.ToString("O"),
                Quarantined = ["container-aaaaaaaaaaaa"],
                Detail = "scripted quarantine",
            }),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.RunAsync(CancellationToken.None));

        var persisted = stateStore.Load().SandboxRecovery;
        Assert.Equal("quarantined", persisted.Status);
        Assert.Equal("container-aaaaaaaaaaaa", Assert.Single(persisted.Quarantined));
        Assert.All(plan.WorkPackages, wp => Assert.Equal("PENDING", wp.Status));
    }

    [Fact]
    public async Task Independent_cleanup_deadline_bounds_a_session_that_ignores_cancellation()
    {
        var session = new HangingSession();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SandboxCleanupDeadline.StopAsync(session, TimeSpan.FromMilliseconds(20)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SandboxCleanupDeadline.DisposeAsync(session, TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void Repository_scope_is_collision_resistant_without_disclosing_the_path()
    {
        using var firstParent = new TempDir();
        using var secondParent = new TempDir();
        var first = Path.Combine(firstParent.Root, "same-name");
        var second = Path.Combine(secondParent.Root, "same-name");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);

        var firstIdentity = SandboxPolicy.RepositoryIdentity(first);
        var secondIdentity = SandboxPolicy.RepositoryIdentity(second);

        Assert.NotEqual(firstIdentity, secondIdentity);
        Assert.StartsWith("same-name-", firstIdentity);
        Assert.DoesNotContain('/', firstIdentity);
        Assert.True(firstIdentity.Length <= 64);
    }

    [Fact]
    public void State_store_rejects_duplicate_or_unbounded_recovery_facts()
    {
        using var directory = new TempDir();
        var path = Path.Combine(directory.Root, "state.json");
        File.WriteAllText(path,
            "{\"sandboxRecovery\":{\"status\":\"not-run\",\"status\":\"clean\"}}");
        Assert.Throws<InvalidOperationException>(() => new StateStore(path).Load());

        var invalid = new RuntimeState
        {
            SandboxRecovery = new SandboxRecoveryInfo
            {
                Status = "quarantined",
                LastRunUtc = DateTimeOffset.UtcNow.ToString("O"),
                Quarantined = [new string('x', 129)],
            },
        };
        Assert.Throws<InvalidOperationException>(() => new StateStore(path).Save(invalid));
    }

    [Fact]
    public void Journal_refuses_a_redirected_storage_file()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        using var repo = Repository();
        using var outside = new TempDir();
        var journalPath = Path.Combine(repo.Root, ".tenninety", "sandbox-resources.json");
        File.CreateSymbolicLink(journalPath, Path.Combine(outside.Root, "target.json"));

        Assert.Throws<InvalidOperationException>(
            () => new SandboxResourceJournal(repo.Root).ReadAll());
        Assert.False(File.Exists(Path.Combine(outside.Root, "target.json")));
    }

    private static SandboxResourceJournal Journal(
        string repository, string managedRoot, string attemptRoot, string containerId)
    {
        var journal = new SandboxResourceJournal(repository);
        var id = journal.Track(
            managedRoot,
            attemptRoot,
            ownedManagedRoot: false,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tenninety.instance"] = "tenninety",
                ["tenninety.repository"] = SandboxPolicy.RepositoryIdentity(repository),
                ["tenninety.run"] = "recovery-test",
                ["tenninety.wp"] = "WP-001",
                ["tenninety.attempt"] = "1",
                ["tenninety.role"] = "tester",
                ["tenninety.candidate"] = new string('b', 40),
            });
        journal.SetContainer(id, containerId);
        return journal;
    }

    private static TenNinetyConfig LiveConfig() => new()
    {
        ProviderMode = "aider",
        Sandbox = new SandboxConfig { Mode = "docker" },
    };

    private static TempDir Repository(bool includeAllRuntimeIgnores = false)
    {
        var repo = new TempDir();
        var git = new GitService(repo.Root);
        git.Init();
        Directory.CreateDirectory(Path.Combine(repo.Root, ".tenninety"));
        File.WriteAllText(
            Path.Combine(repo.Root, ".tenninety", ".gitignore"),
            includeAllRuntimeIgnores
                ? RuntimeGitignoreMigration.Contents
                : "sandbox-resources.json\nsandbox-resources.json.tmp*\n" +
                  "sandbox-resources.json.lock\n");
        git.CommitPaths([".tenninety/.gitignore"], "initial");
        return repo;
    }

    private sealed class RecoveryTransport(string containerId, bool failCleanup)
        : IDockerCliTransport, IDisposable
    {
        public List<DockerCliInvocation> Invocations { get; } = [];

        public Task<DockerCliResult> RunAsync(
            DockerCliInvocation invocation, CancellationToken ct = default)
        {
            Invocations.Add(invocation);
            if (invocation.Arguments[0] == "ps")
                return Task.FromResult(Ok(containerId));
            if (failCleanup)
                throw new InvalidOperationException("scripted cleanup failure");
            if (invocation.Arguments[0] == "inspect")
                return Task.FromResult(new DockerCliResult(
                    1, "", "Error: No such object: " + containerId,
                    false, false, false, TimeSpan.Zero));
            if (invocation.Arguments[0] == "rm")
                return Task.FromResult(new DockerCliResult(
                    1, "", "Error: No such container: " + containerId,
                    false, false, false, TimeSpan.Zero));
            throw new InvalidOperationException("unexpected recovery invocation");
        }

        public void Dispose() { }

        private static DockerCliResult Ok(string stdout) =>
            new(0, stdout, "", false, false, false, TimeSpan.Zero);
    }

    private sealed class HangingSession : ISandboxSession
    {
        public SandboxSessionInfo Info { get; } = new(
            new string('d', 64), SandboxRole.Tester, SandboxSessionState.Running);

        public Task<SandboxCommandResult> RunAsync(
            SandboxCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAsync(CancellationToken ct = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        public ValueTask DisposeAsync() =>
            new(Task.Delay(Timeout.InfiniteTimeSpan));
    }
}
