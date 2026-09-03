using System.Text;
using Tenninety.Core.Models;
using Tenninety.Execution;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Coding;
using Tenninety.Execution.OpenAi;
using Tenninety.Execution.Reviewing;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// Regression tests for the defects repaired during the interrupted-release-gate resume:
///  1. null command arguments fail closed with a controlled error (SandboxTypes);
///  2. the reviewer transcript bound is re-checked after the LAST action output append;
///  3. DockerCli failure messages scrub the host DOCKER_HOST value;
///  4. DockerCliSandboxRuntime attempts bounded label-scoped cleanup when create fails;
///  5. DaemonLock rejects a symlinked lock path and a replaced lock path;
///  6. SandboxRecoveryService treats a positively absent container as already removed;
///  7. PromotionPolicy reports a controlled rejection when a target-tree entry is missing;
///  8. SandboxConfig endpoint/proxy scheme errors never echo the raw value;
///  9. the Coder gate deterministic fixture-command seam reaches the session.
/// </summary>
public sealed class ResumeRepairTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly TempDir _managedRoot = new();
    private readonly GitService _git;

    public ResumeRepairTests()
    {
        _git = new GitService(_repo.Root);
        _git.Init();
        File.WriteAllText(Path.Combine(_repo.Root, "README.md"), "baseline\n");
        _git.CommitAll("initial");
    }

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    // ---- 1. null command arguments fail closed ------------------------------------------

    [Fact]
    public void Sandbox_command_with_a_null_argument_fails_closed_with_a_controlled_error()
    {
        var command = new SandboxCommand
        {
            Executable = "touch",
            Arguments = ["ok", null!],
            WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
            Timeout = TimeSpan.FromMinutes(1),
            MaxOutputBytes = 1_048_576,
        };
        var ex = Assert.Throws<InvalidOperationException>(
            () => command.Validate(TimeSpan.FromMinutes(2)));
        Assert.Contains("null element or a NUL byte", ex.Message);
    }

    // ---- 3. DOCKER_HOST scrubbing --------------------------------------------------------

    [Fact]
    public async Task Docker_cli_failure_messages_scrub_the_host_docker_host_value()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Err("failed while using unix:///run/user/1000/docker.sock"));
        var cli = new DockerCli(transport, key => key == "DOCKER_HOST"
            ? "unix:///run/user/1000/docker.sock"
            : null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => cli.GetVersionAsync());
        Assert.Contains("[docker-host]", ex.Message);
        Assert.DoesNotContain("/run/user/1000/docker.sock", ex.Message);
    }

    // ---- 4. create-failure label-scoped cleanup -------------------------------------------

    [Fact]
    public async Task Runtime_attempts_label_scoped_cleanup_when_create_fails()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson()));
        transport.Enqueue(_ => Err("daemon exploded"));
        transport.Enqueue(_ => Ok(new string('a', 64) + "\n"));   // scoped ps finds one container
        transport.Enqueue(_ => Ok());                             // rm --force succeeds
        transport.Enqueue(_ => Err("Error: No such object: x"));  // absence proof

        var runtime = new DockerCliSandboxRuntime(
            new DockerCli(transport), new SandboxConfig(), _repo.Root, _managedRoot.Root);
        var spec = new SandboxSpec
        {
            Role = SandboxRole.Tester,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "attempt-a")).FullName,
                _managedRoot.Root, _repo.Root),
            Network = SandboxNetworkPolicy.None,
            Cpus = 1.0, MemoryMb = 1024, Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Tester),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.CreateAsync(spec));
        Assert.Contains("daemon exploded", ex.Message);
        var ps = Assert.Single(transport.Invocations, invocation => invocation.Arguments[0] == "ps");
        Assert.Contains(ps.Arguments, argument => argument.StartsWith("label=tenninety.run=", StringComparison.Ordinal));
        Assert.Contains(transport.Invocations,
            invocation => invocation.Arguments.Take(2).SequenceEqual(["rm", "--force"]));
    }

    [Fact]
    public async Task Runtime_rethrows_original_when_create_fails_and_no_leak_is_found()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson()));
        transport.Enqueue(_ => Err("daemon exploded"));
        transport.Enqueue(_ => Ok("")); // scoped ps finds nothing

        var runtime = new DockerCliSandboxRuntime(
            new DockerCli(transport), new SandboxConfig(), _repo.Root, _managedRoot.Root);
        var spec = new SandboxSpec
        {
            Role = SandboxRole.Tester,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "attempt-b")).FullName,
                _managedRoot.Root, _repo.Root),
            Network = SandboxNetworkPolicy.None,
            Cpus = 1.0, MemoryMb = 1024, Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Tester),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.CreateAsync(spec));
        Assert.Contains("daemon exploded", ex.Message);
        Assert.DoesNotContain("could not be proven removed", ex.Message);
    }

    // ---- 5. DaemonLock hardening ----------------------------------------------------------

    [Fact]
    public void Daemon_lock_rejects_a_symlinked_lock_path()
    {
        var lockDir = Path.Combine(DaemonLock.ResolveCommonGitDirectory(_repo.Root), "tenninety");
        Directory.CreateDirectory(lockDir);
        var target = Path.Combine(_repo.Root, "victim.txt");
        File.WriteAllText(target, "keep");
        var lockPath = Path.Combine(lockDir, "daemon.lock");
        Directory.CreateSymbolicLink(lockPath, target);

        var ex = Assert.Throws<InvalidOperationException>(() => DaemonLock.Acquire(_repo.Root));
        Assert.Contains("symlink", ex.Message);
        Assert.Equal("keep", File.ReadAllText(target)); // the victim file was never truncated
    }

    [Fact]
    public void Daemon_lock_detects_an_unlinked_or_replaced_lock_path_on_use()
    {
        var lockDir = Path.Combine(DaemonLock.ResolveCommonGitDirectory(_repo.Root), "tenninety");
        Directory.CreateDirectory(lockDir);
        var lockPath = Path.Combine(lockDir, "daemon.lock");

        var lease = DaemonLock.Acquire(_repo.Root);
        try
        {
            // Simulate unlink-and-recreate while the daemon holds the lock: the path no
            // longer refers to the held inode. The held lease must refuse every further
            // operation (it can never silently keep working against a replaced lock path).
            File.Delete(lockPath);
            File.WriteAllText(lockPath, "replaced");

            var ex = Assert.Throws<InvalidOperationException>(
                () => lease.ThrowIfNotLiveFor(_repo.Root));
            Assert.Contains("unlinked or replaced", ex.Message);
        }
        finally
        {
            lease.Dispose();
        }
    }

    // ---- 7. PromotionPolicy missing target entry ------------------------------------------

    [Fact]
    public void Promotion_policy_rejects_a_change_without_a_target_tree_entry()
    {
        var change = new CandidateChange(
            "src/missing.txt", GitChangeKind.Added, "", "100644", null, null, 1);
        var ex = Assert.Throws<CandidatePolicyRejectedException>(() =>
            PromotionPolicy.Evaluate(
                [change],
                new PromotionPolicyOptions(),
                new Dictionary<string, ScannedEntry>(StringComparer.Ordinal)));
        Assert.Contains("no target-tree entry", ex.Message);
    }

    // ---- 8. SandboxConfig never echoes raw endpoint/proxy values ----------------------------

    [Fact]
    public void Endpoint_scheme_errors_do_not_echo_the_raw_value()
    {
        var sandbox = new SandboxConfig
        {
            Roles = new SandboxRolesConfig
            {
                Coder = new CoderSandboxRoleConfig
                {
                    Image = "sha256:" + new string('a', 64),
                    ModelEndpoint = "ftp://user:secret@host:21/x",
                },
                Reviewer = new ReviewerSandboxRoleConfig { Image = "sha256:" + new string('b', 64) },
                Tester = new TesterSandboxRoleConfig { Image = "sha256:" + new string('c', 64) },
            },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateLiveDocker());
        Assert.Contains("model_endpoint", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public void Proxy_scheme_errors_do_not_echo_the_raw_value()
    {
        var sandbox = new SandboxConfig();
        sandbox.Roles.Tester.Restore.Enabled = true;
        sandbox.Roles.Tester.Restore.NetworkName = "tenninety-restore-test";
        sandbox.Roles.Tester.Restore.ProxyUrl = "ftp://user:secret@host:21/";
        sandbox.Roles.Tester.Restore.ApprovedFeeds = ["https://api.nuget.org/v3/index.json"];
        var ex = Assert.Throws<InvalidOperationException>(() => sandbox.ValidateStructural());
        Assert.Contains("proxy_url", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    // ---- 9. Coder gate deterministic fixture-command seam -----------------------------------

    [Fact]
    public async Task Coder_gate_runs_the_deterministic_fixture_command_seam()
    {
        var repo = new TestGitRepo();
        repo.WriteFile(".gitignore", ".tenninety/\n");
        repo.WriteFile("README.md", "baseline\n");
        repo.Commit("baseline");
        var mainSha = repo.Git.HeadSha();
        repo.Git.CreateAndCheckoutBranch("work/WP-001");
        using var lease = DaemonLock.Acquire(repo.Root);
        var config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            CoderAgent = "aider",
            LocalModels = new LocalModelsConfig { Coder = "coder-model", Reviewer = "reviewer-model" },
            Sandbox = new SandboxConfig
            {
                WorkspaceRoot = _managedRoot.Root,
                Roles = new SandboxRolesConfig
                {
                    Coder = new CoderSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.CoderImageId,
                        ModelEndpoint = "http://coder-model:8000/v1",
                    },
                    Reviewer = new ReviewerSandboxRoleConfig { Image = PreflightFakeTransport.ReviewerImageId },
                    Tester = new TesterSandboxRoleConfig { Image = PreflightFakeTransport.TesterImageId },
                },
            },
        };
        var timeline = new List<string>();
        var runtime = new SandboxTesterGateTests.RecordingRuntime();
        runtime.SessionFactory = spec => new RecordingSandboxSession
        {
            Role = spec.Role,
            SourcePath = spec.HostWorkspacePath!.Value,
            EventSink = timeline.Add,
            OnRun = command =>
            {
                timeline.Add("command:" + string.Join(" ", command.Arguments));
                File.WriteAllText(Path.Combine(spec.HostWorkspacePath!.Value, "fixture.txt"), "ok\n");
            },
        };
        var fake = new PreflightFakeTransport(config.Sandbox);
        var transport = new SandboxTesterGateTests.ForwardingTransport(fake, timeline.Add);
        var gate = new SandboxCoderGate(
            repo.Git, config, lease, log: null,
            transportFactory: () => transport,
            runtimeFactory: (_, _) => runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, config.Sandbox, root, repo.Root),
            deleteWorkspaceOverride: path =>
            {
                timeline.Add("delete");
                SandboxTesterGate.DeleteAttemptDirectory(path, _managedRoot.Root);
                return Task.CompletedTask;
            },
            coderCommandFactory: _ => new SandboxCommand
            {
                Executable = "touch",
                Arguments = ["/workspace/fixture-seam.txt"],
                WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
                Timeout = TimeSpan.FromMinutes(2),
                MaxOutputBytes = 1_048_576,
            });

        var result = await gate.ImplementAsync(new CoderRunContext
        {
            Candidate = new CandidateRevision("work/WP-001", mainSha, mainSha),
            WorkPackage = new WorkPackage
            {
                Id = "WP-001",
                Title = "Seam",
                Goal = "Fixture command",
                Directives = ["Touch one file"],
                AcceptanceCriteria = ["Promoted"],
            },
            Attempt = 1,
        });

        Assert.True(result.ProducedChanges, result.Summary);
        Assert.True(File.Exists(Path.Combine(repo.Root, "fixture.txt")));
        // The seam command (not the production aider plan) reached the session.
        Assert.Contains("command:/workspace/fixture-seam.txt", timeline);
        Assert.DoesNotContain(
            timeline, entry => entry.Contains("/usr/local/bin/aider", StringComparison.Ordinal));
        Assert.True(transport.Disposed);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static string ImageInspectJson() =>
        "[{\"Id\":\"sha256:" + new string('a', 64) +
        "\",\"RepoDigests\":[],\"Config\":{\"User\":\"1000:1000\",\"Entrypoint\":[]}}]";

    private static DockerCliResult Ok(string stdout = "") =>
        new(0, stdout, "", TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Err(string stderr = "error") =>
        new(1, "", stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));
}
