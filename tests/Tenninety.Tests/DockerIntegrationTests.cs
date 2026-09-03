using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Opt-in Docker integration tests. They are DISCOVERED always (filterable with
/// <c>--filter Category=Docker</c>) and reported skipped with a clear reason unless
/// <c>TENNINETY_RUN_DOCKER_TESTS=1</c>. Once opted in, a missing, malformed, or unavailable
/// TENNINETY_TEST_IMAGE fails the test (never silently skips).
///
/// Minimum executable contract of TENNINETY_TEST_IMAGE: a LOCALLY PRESENT Linux image whose
/// exact ID equals the provided sha256 value, declaring an explicit numeric non-root user
/// (e.g. USER 1000:1000) with no ENTRYPOINT, and containing the POSIX utilities `sleep` and
/// `touch`. Nothing else is assumed and no image is ever pulled.
///
/// Every test uses the offline Reviewer role (network policy none — no pre-created model
/// network is required), uniquely labelled disposable containers, and disposable temporary
/// workspaces. Final absence after disposal is proven with an explicit typed inspect; all
/// resources are removed in finally blocks and unproven cleanup fails the test.
/// </summary>
[Trait("Category", "Docker")]
public class DockerIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Full_create_exec_stop_remove_lifecycle_with_final_absence_proof()
    {
        var imageInfo = await DockerTestHelper.ResolveTestImageAsync();
        var harness = await IntegrationHarness.StartAsync(imageInfo);
        try
        {
            var session = await harness.CreateSessionAsync();
            try
            {
                Assert.Equal(SandboxSessionState.Running, session.Info.State);

                // Exec proves a writable workspace through the real bind mount.
                var exec = await session.RunAsync(new SandboxCommand
                {
                    Executable = "touch",
                    Arguments = ["/workspace/integration-marker"],
                });
                Assert.True(exec.Succeeded,
                    $"touch /workspace failed: exit={exec.ExitCode} err={exec.StdErrTail}");
                Assert.True(Directory.Exists(harness.WorkspacePath));
                Assert.True(File.Exists(Path.Combine(harness.WorkspacePath, "integration-marker")));

                // Graceful stop must prove quiescence through inspect.
                await session.StopAsync();
                Assert.Equal(SandboxSessionState.StoppedQuiescent, session.Info.State);
                Assert.True(((ISandboxSession)session).WritesQuiescent);
            }
            finally
            {
                await session.DisposeAsync();
            }

            // Disposed state is only reached after the typed remove succeeded (or absence
            // was positively established); prove final absence EXPLICITLY.
            Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
            Assert.Null(await harness.Cli.TryInspectContainerAsync(session.Info.ContainerId));
        }
        finally
        {
            await harness.DisposeAndVerifyAsync();
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Container_root_is_read_only_while_tmp_and_home_are_writable()
    {
        var imageInfo = await DockerTestHelper.ResolveTestImageAsync();
        var harness = await IntegrationHarness.StartAsync(imageInfo);
        try
        {
            var session = await harness.CreateSessionAsync();
            try
            {
                var rootWrite = await session.RunAsync(new SandboxCommand
                {
                    Executable = "touch", Arguments = ["/integration-root-write"],
                });
                Assert.NotEqual(0, rootWrite.ExitCode);

                var tmpWrite = await session.RunAsync(new SandboxCommand
                {
                    Executable = "touch", Arguments = ["/tmp/integration-tmp-write"],
                });
                Assert.True(tmpWrite.Succeeded);

                var homeWrite = await session.RunAsync(new SandboxCommand
                {
                    Executable = "touch",
                    Arguments = [SandboxPolicy.ContainerHomePath + "/integration-home-write"],
                });
                Assert.True(homeWrite.Succeeded);
            }
            finally
            {
                await session.DisposeAsync();
            }
            Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
            Assert.Null(await harness.Cli.TryInspectContainerAsync(session.Info.ContainerId));
        }
        finally
        {
            await harness.DisposeAndVerifyAsync();
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Effective_inspection_proves_hardening_settings_and_offline_network()
    {
        var imageInfo = await DockerTestHelper.ResolveTestImageAsync();
        var harness = await IntegrationHarness.StartAsync(imageInfo);
        try
        {
            var session = await harness.CreateSessionAsync();
            try
            {
                var detailed = await harness.Cli.InspectContainerDetailedAsync(session.Info.ContainerId);

                Assert.Equal(imageInfo.ImageId, detailed.ImageId);
                Assert.Equal(harness.Identity.ToUserFlag(), detailed.User);
                Assert.True(detailed.Running);
                Assert.True(detailed.ReadonlyRootfs);
                Assert.Contains("ALL", detailed.CapDrop);
                Assert.Empty(detailed.CapAdd);
                Assert.False(detailed.Privileged);
                Assert.Contains(detailed.SecurityOpt, o =>
                    o.Equals("no-new-privileges", StringComparison.OrdinalIgnoreCase) ||
                    o.StartsWith("no-new-privileges:", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(detailed.SecurityOpt, o =>
                    o.Contains("unconfined", StringComparison.OrdinalIgnoreCase));
                Assert.NotEqual("host", detailed.PidMode);
                Assert.NotEqual("host", detailed.IpcMode);
                Assert.Equal(0, detailed.DeviceCount);
                Assert.Equal(0, detailed.PortBindingCount);
                Assert.Equal(1_000_000_000L, detailed.NanoCpus);         // 1.0 CPU
                Assert.Equal(256L * 1024 * 1024, detailed.MemoryBytes);  // 256 MB
                Assert.Equal(64, detailed.PidsLimit);
                Assert.Contains(detailed.Ulimits, u =>
                    u.Name == "nofile" && u.Soft == 4096 && u.Hard == 8192);

                // Offline proof: the effective network mode is exactly "none".
                Assert.Equal("none", detailed.NetworkMode);

                // Exactly one WRITABLE bind mount: the disposable workspace at /workspace.
                var binds = detailed.Mounts.Where(m => m.Type == "bind").ToList();
                var mount = Assert.Single(binds);
                Assert.Equal("bind", mount.Type);
                Assert.Equal(harness.RevalidatedWorkspace, mount.Source);
                Assert.Equal("/workspace", mount.Destination);
                Assert.True(mount.Rw);
                Assert.Equal("rprivate", mount.Propagation);

                // Exact bounded tmpfs options and the /workspace workdir.
                foreach (var expected in SandboxPolicy.FixedTmpfsMounts)
                    Assert.Equal(expected.Options, detailed.Tmpfs[expected.ContainerPath]);
                Assert.Equal("/workspace", detailed.WorkingDir);
            }
            finally
            {
                await session.DisposeAsync();
            }
            Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
            Assert.Null(await harness.Cli.TryInspectContainerAsync(session.Info.ContainerId));
        }
        finally
        {
            await harness.DisposeAndVerifyAsync();
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Timeout_terminates_the_whole_container_and_proves_it()
    {
        var imageInfo = await DockerTestHelper.ResolveTestImageAsync();
        var harness = await IntegrationHarness.StartAsync(imageInfo);
        try
        {
            var session = await harness.CreateSessionAsync();
            try
            {
                var result = await session.RunAsync(new SandboxCommand
                {
                    Executable = "sleep",
                    Arguments = ["60"],
                    Timeout = TimeSpan.FromSeconds(3),
                });
                Assert.True(result.TimedOut);
                Assert.False(result.Succeeded);
                Assert.NotEqual(SandboxSessionState.Running, session.Info.State);

                // The WHOLE container must be gone or not running — proven by a typed inspect.
                var state = await harness.Cli.TryInspectContainerAsync(session.Info.ContainerId);
                Assert.True(state is null || !state.Running);
            }
            finally
            {
                await session.DisposeAsync();
            }
            Assert.Equal(SandboxSessionState.Disposed, session.Info.State);
            Assert.Null(await harness.Cli.TryInspectContainerAsync(session.Info.ContainerId));
        }
        finally
        {
            await harness.DisposeAndVerifyAsync();
        }
    }

    // ---- harness ---------------------------------------------------------------------------

    private sealed class IntegrationHarness : IAsyncDisposable
    {
        public DockerCli Cli { get; private set; } = null!;
        public DockerCliProcessTransport Transport { get; private set; } = null!;
        public ContainerIdentity Identity { get; private set; } = null!;
        public string WorkspacePath { get; private set; } = "";
        public string RevalidatedWorkspace { get; private set; } = "";

        private readonly string _root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"tenninety-docker-it-{Guid.NewGuid():N}")).FullName;
        private DockerImageInfo _imageInfo = null!;

        public static Task<IntegrationHarness> StartAsync(DockerImageInfo imageInfo) =>
            Task.FromResult(new IntegrationHarness { _imageInfo = imageInfo });

        public async Task<DockerCliSandboxSession> CreateSessionAsync()
        {
            Transport = new DockerCliProcessTransport();
            Cli = new DockerCli(Transport);
            Identity = ContainerIdentity.Parse(_imageInfo.ConfiguredUser);

            var workspaceDir = Directory.CreateDirectory(Path.Combine(_root, "workspace"));
            var repoDir = Directory.CreateDirectory(Path.Combine(_root, "repo"));
            File.WriteAllText(Path.Combine(repoDir.FullName, "marker"), "authoritative-stand-in");
            WorkspacePath = workspaceDir.FullName;

            // Offline Reviewer role: network policy none — no pre-created model network is
            // required, and the effective NetworkMode must inspect as exactly "none".
            RevalidatedWorkspace = ValidatedSandboxWorkspacePath.Create(
                workspaceDir.FullName, _root, repoDir.FullName).Value;

            var spec = new SandboxSpec
            {
                Role = SandboxRole.Reviewer,
                Image = _imageInfo.ImageId,
                HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                    workspaceDir.FullName, _root, repoDir.FullName),
                Network = SandboxNetworkPolicy.None,
                Cpus = 1.0, MemoryMb = 256, Pids = 64,
                Timeout = TimeSpan.FromMinutes(5),
                Labels = new Dictionary<string, string>
                {
                    ["tenninety.instance"] = "integration-test",
                    ["tenninety.repository"] = "integration-repo",
                    ["tenninety.run"] = "it-" + Guid.NewGuid().ToString("N")[..8],
                    ["tenninety.wp"] = "IT-001",
                    ["tenninety.attempt"] = "1",
                    ["tenninety.role"] = "reviewer",
                },
            };
            var runtime = new DockerCliSandboxRuntime(
                Cli, new SandboxConfig(), repoDir.FullName, _root);
            return (DockerCliSandboxSession)await runtime.CreateAsync(spec);
        }

        /// <summary>Deletes the disposable workspace and fails when the deletion is unproven.</summary>
        public async Task DisposeAndVerifyAsync()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                Assert.False(Directory.Exists(_root), "the disposable workspace must be deleted");
            }
            finally
            {
                Transport.Dispose();
            }
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => DisposeAndVerifyAsync().ToValueTask();
    }
}

internal static class TaskToValueTaskExtensions
{
    public static ValueTask ToValueTask(this Task task) => new(task);
}
