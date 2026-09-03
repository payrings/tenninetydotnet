using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Tests for the production <see cref="DockerCliSandboxRuntime"/>: the complete exact create
/// vector, forbidden tokens (combined and separate), exact CPU formatting, network mapping
/// rejection, immediate workspace revalidation ordering (symlink swap and comma injection),
/// registry digest evidence, malformed identifiers, and cleanup failure surfacing.
/// </summary>
public class DockerCliSandboxRuntimeTests : IDisposable
{
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _repo = new();
    private readonly string _imageId = "sha256:" + new string('a', 64);
    private readonly string _containerId = new('1', 64);

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private static string ImageInspectJson(string imageId, string user = "1000:1000",
        string[]? repoDigests = null) =>
        "[{\"Id\":\"" + imageId + "\",\"RepoDigests\":[" +
        string.Join(",", (repoDigests ?? []).Select(d => "\"" + d + "\"")) +
        "],\"Config\":{\"User\":\"" + user + "\",\"Entrypoint\":[]}}]";

    private static string ContainerInspectJson(string containerId, string imageId, bool running = true) =>
        "[{\"Id\":\"" + containerId + "\",\"Image\":\"" + imageId +
        "\",\"State\":{\"Running\":" + (running ? "true" : "false") +
        ",\"Paused\":false,\"OOMKilled\":false,\"ExitCode\":0},\"Config\":{},\"HostConfig\":{}}]";

    private ValidatedSandboxWorkspacePath MakeWorkspace(string name = "attempt-1")
    {
        var dir = Directory.CreateDirectory(Path.Combine(_managedRoot.Root, name));
        return ValidatedSandboxWorkspacePath.Create(dir.FullName, _managedRoot.Root, _repo.Root);
    }

    private SandboxSpec MakeSpec(SandboxNetworkPolicy network = SandboxNetworkPolicy.None,
        double cpus = 0.25, string? image = null)
    {
        return new SandboxSpec
        {
            Role = SandboxRole.Tester,
            Image = image ?? "sha256:" + new string('a', 64),
            HostWorkspacePath = MakeWorkspace(),
            Network = network,
            Cpus = cpus,
            MemoryMb = 1024,
            Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Tester),
        };
    }

    private DockerCliSandboxRuntime MakeRuntime(ScriptedTransport transport, SandboxConfig? config = null) =>
        new(new DockerCli(transport), config ?? new SandboxConfig(), _repo.Root, _managedRoot.Root);

    // ---- create/start success + exact positive vector ----------------------------

    [Fact]
    public async Task CreateAsync_builds_the_complete_exact_hardened_vector()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(ContainerInspectJson(_containerId, _imageId)));

        var runtime = MakeRuntime(transport);
        var session = await runtime.CreateAsync(MakeSpec());

        Assert.Equal(SandboxRole.Tester, session.Info.Role);
        Assert.Equal(SandboxSessionState.Running, session.Info.State);

        // Inspect, create, start, inspect: the create vector must be exact.
        Assert.Equal(["image", "inspect", "sha256:" + new string('a', 64)],
            transport.Invocations[0].Arguments);
        var createArgs = transport.Invocations[1].Arguments.ToList();
        Assert.Equal("create", createArgs[0]);

        var expected = new List<string>
        {
            "create",
            "--name", FindName(createArgs),
            "--pull=never",
            "--read-only",
            "--cap-drop=ALL",
            "--security-opt=no-new-privileges",
            "--workdir", "/workspace",
            "--cpus", "0.25",
            "--memory", "1024m",
            "--pids-limit", "128",
            "--ulimit", "nofile=4096:8192",
            "--network", "none",
            "--user", "1000:1000",
            "--tmpfs", "/tmp:size=512m,nosuid,nodev,noexec",
            "--tmpfs", "/home/tenninety:size=256m,nosuid,nodev",
            "--label", "tenninety.attempt=1",
            "--label", "tenninety.instance=test-instance",
            "--label", "tenninety.repository=demo-repository",
            "--label", "tenninety.role=tester",
            "--label", "tenninety.run=run-0001",
            "--label", "tenninety.wp=WP-001",
            "--mount", "type=bind,source=" + Path.Combine(_managedRoot.Root, "attempt-1") +
                       ",target=/workspace,bind-propagation=rprivate",
            _imageId,
            "sleep",
            "infinity",
        };
        Assert.Equal(expected, createArgs);

        Assert.Equal(["start", _containerId], transport.Invocations[2].Arguments);
        Assert.Equal(["inspect", _containerId], transport.Invocations[3].Arguments);
    }

    private static string FindName(List<string> args)
    {
        var idx = args.IndexOf("--name");
        var name = args[idx + 1];
        Assert.Matches("^tenninety-tester-[0-9a-f]{32}$", name);
        return name;
    }

    [Fact]
    public async Task Create_vector_contains_no_forbidden_token_in_combined_or_separate_form()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(ContainerInspectJson(_containerId, _imageId)));
        await MakeRuntime(transport).CreateAsync(MakeSpec());

        var args = transport.Invocations[1].Arguments.ToList();
        string[] forbiddenCombined =
        [
            "--privileged", "--network=host", "--pid=host", "--ipc=host",
            "seccomp=unconfined", "--gpus", "--device", "--publish", "-p", "-v",
            "--cap-add", "--security-opt=seccomp=unconfined", "--network=bridge",
        ];
        foreach (var token in forbiddenCombined)
            Assert.DoesNotContain(token, args);
        // Separate-form forbidden values: no argument is the bare host/privileged value and
        // no value after --network/--pid/--ipc is a host-style setting.
        Assert.DoesNotContain("privileged", args);
        Assert.DoesNotContain("host", args);
        Assert.DoesNotContain("unconfined", args);
        var networkValue = args[args.IndexOf("--network") + 1];
        Assert.Equal("none", networkValue);
        // Exactly one --mount and no -v flag anywhere.
        Assert.Equal(1, args.Count(a => a == "--mount"));
        Assert.DoesNotContain("-v", args);
        // No device, port, or docker socket reference anywhere in any element.
        Assert.All(args, a => Assert.DoesNotContain("docker.sock", a));
        Assert.All(args, a => Assert.DoesNotContain("/dev/", a));
    }

    [Fact]
    public async Task Cpu_value_0_25_remains_0_25_in_the_vector()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(ContainerInspectJson(_containerId, _imageId)));
        await MakeRuntime(transport).CreateAsync(MakeSpec(cpus: 0.25));

        var args = transport.Invocations[1].Arguments.ToList();
        var idx = args.IndexOf("--cpus");
        Assert.Equal("0.25", args[idx + 1]);
    }

    [Fact]
    public async Task Coder_maps_to_the_validated_model_network()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(ContainerInspectJson(_containerId, _imageId)));

        var spec = new SandboxSpec
        {
            Role = SandboxRole.Coder,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = MakeWorkspace(),
            Network = SandboxNetworkPolicy.Model,
            Cpus = 1.0, MemoryMb = 1024, Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder),
        };
        await MakeRuntime(transport).CreateAsync(spec);

        var args = transport.Invocations[1].Arguments.ToList();
        Assert.Equal("tenninety-coder-model", args[args.IndexOf("--network") + 1]);
    }

    // ---- network mapping rejection --------------------------------------------------

    [Fact]
    public async Task Reserved_model_network_is_rejected_before_any_docker_call()
    {
        var transport = new ScriptedTransport();
        var runtime = MakeRuntime(transport,
            new SandboxConfig { ModelNetwork = "host" });
        var spec = new SandboxSpec
        {
            Role = SandboxRole.Coder,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = MakeWorkspace(),
            Network = SandboxNetworkPolicy.Model,
            Cpus = 1.0, MemoryMb = 1024, Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.CreateAsync(spec));
        Assert.Contains("not a permitted Docker network", ex.Message);
        Assert.Empty(transport.Invocations);
    }

    [Fact]
    public async Task Restore_network_without_a_valid_configured_name_is_rejected()
    {
        var transport = new ScriptedTransport();
        var runtime = MakeRuntime(transport, new SandboxConfig
        {
            Roles = { Tester = { Restore = { Enabled = true, NetworkName = "" } } },
        });
        var spec = new SandboxSpec
        {
            Role = SandboxRole.Restore,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = MakeWorkspace(),
            Network = SandboxNetworkPolicy.Restore,
            Cpus = 1.0, MemoryMb = 1024, Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Restore),
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(spec));
        Assert.Empty(transport.Invocations);
    }

    // ---- revalidation ordering -------------------------------------------------------

    [Fact]
    public async Task Workspace_swapped_to_a_symlink_after_image_inspect_is_rejected_before_create()
    {
        var workspaceDir = Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "attempt-swap"));
        var outside = Directory.CreateDirectory(Path.Combine(_repo.Root, "outside")).FullName;
        var transport = new ScriptedTransport();
        transport.Enqueue(_ =>
        {
            // TOCTOU simulation: between the earlier validation and the create call, the
            // workspace directory is replaced by a symlink pointing outside the managed root.
            workspaceDir.Delete();
            Directory.CreateSymbolicLink(workspaceDir.FullName, outside);
            return Ok(ImageInspectJson(_imageId));
        });
        transport.Enqueue(_ => throw new InvalidOperationException("create must never be reached"));

        var spec = new SandboxSpec
        {
            Role = SandboxRole.Tester,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                workspaceDir.FullName, _managedRoot.Root, _repo.Root),
            Network = SandboxNetworkPolicy.None,
            Cpus = 1.0, MemoryMb = 1024, Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Tester),
        };
        var runtime = MakeRuntime(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(spec));

        Assert.Single(transport.Invocations); // image inspect only; create never happened
        Assert.Equal("image", transport.Invocations[0].Arguments[0]);
    }

    [Fact]
    public async Task Mount_source_with_a_comma_is_rejected_before_create()
    {
        Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "attempt,a,b"));
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => throw new InvalidOperationException("create must never be reached"));

        var spec = new SandboxSpec
        {
            Role = SandboxRole.Tester,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = MakeWorkspace("attempt,a,b"),
            Network = SandboxNetworkPolicy.None,
            Cpus = 1.0, MemoryMb = 1024, Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Tester),
        };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakeRuntime(transport).CreateAsync(spec));
        Assert.Contains("--mount", ex.Message);
        Assert.Single(transport.Invocations); // image inspect only; create never happened
    }

    // ---- image identity and digest evidence ------------------------------------------

    [Fact]
    public async Task Exact_local_image_mismatch_is_rejected()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson("sha256:" + new string('b', 64))));
        var runtime = MakeRuntime(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Single(transport.Invocations);
    }

    [Fact]
    public async Task Registry_digest_reference_creates_from_the_exact_inspected_image_id()
    {
        var digest = new string('c', 64);
        var reference = "registry.example.com/team/img@sha256:" + digest;
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId, repoDigests:
            ["registry.example.com/team/img@sha256:" + digest])));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(ContainerInspectJson(_containerId, _imageId)));

        await MakeRuntime(transport).CreateAsync(MakeSpec(image: reference));

        Assert.Equal(["image", "inspect", reference], transport.Invocations[0].Arguments);
        Assert.Contains(_imageId, transport.Invocations[1].Arguments); // exact inspected ID used
    }

    [Fact]
    public async Task Registry_digest_missing_evidence_is_rejected()
    {
        var digest = new string('c', 64);
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId, repoDigests: [])));
        var runtime = MakeRuntime(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.CreateAsync(MakeSpec(image: "registry.example.com/team/img@sha256:" + digest)));
        Assert.Single(transport.Invocations);
    }

    [Fact]
    public async Task Malformed_inspected_image_id_is_rejected()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson("sha256:zzz-not-hex")));
        var runtime = MakeRuntime(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Single(transport.Invocations);
    }

    [Fact]
    public async Task Root_identity_is_rejected()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId, user: "0")));
        var runtime = MakeRuntime(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Contains("uid=0", ex.Message);
        Assert.Single(transport.Invocations);
    }

    // ---- start/inspect failures and cleanup --------------------------------------------

    [Fact]
    public async Task Start_failure_with_successful_cleanup_surfaces_the_primary_error()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Err("start failed"));
        transport.Enqueue(_ => Ok());                          // rm --force succeeds
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence proof

        var runtime = MakeRuntime(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Contains("container start failed", ex.Message);
        Assert.DoesNotContain("Cleanup also failed", ex.Message);
        Assert.Equal(["rm", "--force", _containerId], transport.Invocations[3].Arguments);
    }

    [Fact]
    public async Task Start_failure_with_cleanup_failure_surfaces_both_errors()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Err("start failed"));
        transport.Enqueue(_ => Err("rm failed"));
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence also unprovable

        var runtime = MakeRuntime(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Contains("container start failed", ex.Message);
        Assert.Contains("Cleanup also failed", ex.Message);
        Assert.Contains("rm failed", ex.Message);
    }

    [Fact]
    public async Task Started_container_not_running_fails_and_cleans_up()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(ContainerInspectJson(_containerId, _imageId, running: false)));
        transport.Enqueue(_ => Ok());                            // cleanup rm
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence proof

        var runtime = MakeRuntime(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Contains("not running", ex.Message);
        Assert.Equal(6, transport.Invocations.Count);
    }

    [Fact]
    public async Task Started_container_with_an_image_mismatch_fails_and_cleans_up()
    {
        var otherImage = "sha256:" + new string('b', 64);
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok(_containerId + "\n"));
        transport.Enqueue(_ => Ok());
        transport.Enqueue(_ => Ok(ContainerInspectJson(_containerId, otherImage)));
        transport.Enqueue(_ => Ok());                            // cleanup rm
        transport.Enqueue(_ => Err("Error: No such object: x")); // absence proof

        var runtime = MakeRuntime(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Contains("does not match the resolved exact image id", ex.Message);
        Assert.Equal(6, transport.Invocations.Count);
    }

    [Fact]
    public async Task Malformed_container_id_from_create_is_rejected_before_start()
    {
        var transport = new ScriptedTransport();
        transport.Enqueue(_ => Ok(ImageInspectJson(_imageId)));
        transport.Enqueue(_ => Ok("short-id\n"));
        // After the malformed create output, the runtime attempts bounded label-scoped
        // cleanup; the scripted transport has no further handlers, so the cleanup list call
        // reuses the last handler and the scoped remove is unprovable (surfaced, never a start).

        var runtime = MakeRuntime(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.CreateAsync(MakeSpec()));
        Assert.Contains("container id", ex.Message);
        Assert.True(transport.Invocations.Count >= 2); // image inspect + create (+ scoped cleanup attempt)
        Assert.DoesNotContain(transport.Invocations, invocation => invocation.Arguments[0] == "start");
    }

    // ---- helpers -----------------------------------------------------------------------

    private static DockerCliResult Ok(string stdout = "") =>
        new(0, stdout, "", TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Err(string stderr = "error") =>
        new(1, "", stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));
}

/// <summary>Scripted transport with per-call handlers that can perform side effects.</summary>
public sealed class ScriptedTransport : IDockerCliTransport
{
    private readonly List<Func<DockerCliInvocation, DockerCliResult>> _handlers = new();
    public readonly List<DockerCliInvocation> Invocations = new();

    public int CallCount => Invocations.Count;

    public void Enqueue(Func<DockerCliInvocation, DockerCliResult> handler) => _handlers.Add(handler);

    /// <summary>Replaces the handler registered for a zero-based call index.</summary>
    public void ReplaceHandler(int index, Func<DockerCliInvocation, DockerCliResult> handler) =>
        _handlers[index] = handler;

    /// <summary>Inserts an additional handler at the given zero-based call position,
    /// shifting later handlers back.</summary>
    public void InsertHandler(int index, Func<DockerCliInvocation, DockerCliResult> handler) =>
        _handlers.Insert(index, handler);

    public Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default)
    {
        Invocations.Add(invocation);
        var handler = _handlers[Math.Min(Invocations.Count - 1, _handlers.Count - 1)];
        return Task.FromResult(handler(invocation));
    }
}
