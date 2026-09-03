using System.Reflection;
using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Tests for the typed <see cref="DockerCli"/> adapter: every operation's FULL deterministic
/// argument vector is asserted, typed requests are constructible ONLY through their validated
/// factories (no direct-construction or post-validation-mutation bypass), identifiers fail
/// closed, absence is distinguished from operational failure, removal REQUIRES a final
/// absence proof (a container still present after rm is a contradiction), list output IDs are
/// validated, registry digest evidence is enforced, duplicate JSON fields are rejected, and
/// failure messages are bounded and scrubbed.
/// </summary>
public class DockerCliTests : IDisposable
{
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _repo = new();
    private static readonly string ImageId = "sha256:" + new string('a', 64);
    private static readonly string OtherImageId = "sha256:" + new string('b', 64);
    private static readonly string ContainerId = new('1', 64);

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private static DockerCliResult Ok(string stdout = "") =>
        new(0, stdout, "", TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Err(string stderr = "error") =>
        new(1, "", stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult TimedOutErr() =>
        new(-1, "", "", TimedOut: true, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static string ImageInspectJson(string id, string user = "1000:1000",
        string[]? repoDigests = null, string[]? entrypoint = null) =>
        "[{\"Id\":\"" + id + "\",\"RepoDigests\":[" +
        string.Join(",", (repoDigests ?? []).Select(d => "\"" + d + "\"")) +
        "],\"Config\":{\"User\":\"" + user + "\",\"Entrypoint\":[" +
        string.Join(",", (entrypoint ?? []).Select(e => "\"" + e + "\"")) + "]}}]";

    private static string ContainerInspectJson(string id, string image, bool running = true,
        bool oom = false, int exit = 0) =>
        "[{\"Id\":\"" + id + "\",\"Image\":\"" + image +
        "\",\"State\":{\"Running\":" + (running ? "true" : "false") +
        ",\"Paused\":false,\"OOMKilled\":" + (oom ? "true" : "false") +
        ",\"ExitCode\":" + exit + "},\"Config\":{},\"HostConfig\":{}}]";

    // ---- valid spec/evidence/request fixtures ---------------------------------

    private SandboxSpec MakeSpec(SandboxRole role = SandboxRole.Tester,
        SandboxNetworkPolicy network = SandboxNetworkPolicy.None,
        Dictionary<string, string>? labels = null,
        Dictionary<string, string>? environment = null)
    {
        var workspace = Directory.CreateDirectory(
            Path.Combine(_managedRoot.Root, "attempt-" + Guid.NewGuid().ToString("N")));
        return new SandboxSpec
        {
            Role = role,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                workspace.FullName, _managedRoot.Root, _repo.Root),
            Network = network,
            Cpus = 0.25,
            MemoryMb = 1024,
            Pids = 128,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = labels ?? SandboxAbstractionTests.CompleteLabels(role),
            Environment = environment ?? new Dictionary<string, string>(),
        };
    }

    /// <summary>Trusted resolution config used by tests: validated model and restore names.</summary>
    internal static readonly SandboxConfig TestConfig = new()
    {
        ModelNetwork = "tenninety-coder-model",
        Roles = { Tester = { Restore = { NetworkName = "tenninety-restore" } } },
    };

    private DockerCreateRequest MakeRequest(
        SandboxSpec? spec = null,
        string? exactImageId = null,
        string? containerName = null,
        string? source = null,
        SandboxConfig? config = null,
        string user = "1000:1000")
    {
        spec ??= MakeSpec();
        var evidence = spec.ValidateAndCapture();
        var workspaceSource = source ?? Path.Combine(_managedRoot.Root, "attempt-src");
        return DockerCreateRequest.FromSpec(
            evidence,
            config ?? TestConfig,
            exactImageId ?? ImageId,
            ContainerIdentity.Parse(user),
            containerName ?? "tenninety-tester-" + new string('0', 32),
            workspaceSource);
    }

    private static SandboxCommand MakeCommand(
        string executable = "true",
        IReadOnlyList<string>? arguments = null,
        string workingDirectory = "/workspace",
        string? stdIn = null,
        long maxOutputBytes = 4096,
        Dictionary<string, string>? environment = null) =>
        new()
        {
            Executable = executable,
            Arguments = arguments ?? [],
            WorkingDirectory = workingDirectory,
            StdIn = stdIn,
            Timeout = TimeSpan.FromMinutes(1),
            MaxOutputBytes = maxOutputBytes,
            Environment = environment ?? new Dictionary<string, string>(),
        };

    // ---- typed requests are factory-only (no construction bypass) -----------------

    [Fact]
    public void Typed_request_types_have_no_public_constructor_or_mutable_surface()
    {
        foreach (var type in new[]
                 {
                     typeof(DockerCreateRequest), typeof(DockerExecRequest), typeof(DockerContainerScope),
                 })
        {
            Assert.False(type.IsPublic, $"{type.Name} must be internal.");
            Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
            Assert.All(type.GetProperties(), p =>
                Assert.False(p.SetMethod is { IsPublic: true },
                    $"{type.Name}.{p.Name} must not have a public setter."));
        }
        // The adapter operations that accept these types are internal too.
        Assert.True(typeof(DockerCli)
            .GetMethod("CreateContainerAsync", BindingFlags.NonPublic | BindingFlags.Instance) is not null);
        Assert.True(typeof(DockerCli)
            .GetMethod("ExecAsync", BindingFlags.NonPublic | BindingFlags.Instance) is not null);
        Assert.True(typeof(DockerCli)
            .GetMethod("ListContainersAsync", BindingFlags.NonPublic | BindingFlags.Instance) is not null);
    }

    [Fact]
    public void CreateRequest_requires_validation_evidence_and_rejects_unvalidated_values()
    {
        // Root identity, malformed image, unsafe mount source, incomplete labels, forbidden
        // environment key, and invalid resources all fail before any request exists.
        Assert.Throws<InvalidOperationException>(() => DockerCreateRequest.FromSpec(
            MakeSpec().ValidateAndCapture(), TestConfig, ImageId, new ContainerIdentity(0, 0),
            "tenninety-tester-" + new string('0', 32), "/srv/tenninety/src"));
        Assert.Throws<InvalidOperationException>(() => DockerCreateRequest.FromSpec(
            MakeSpec().ValidateAndCapture(), TestConfig, "sha256:short",
            new ContainerIdentity(1000, 1000), "tenninety-tester-" + new string('0', 32),
            "/srv/tenninety/src"));
        Assert.Throws<InvalidOperationException>(() => DockerCreateRequest.FromSpec(
            MakeSpec().ValidateAndCapture(), TestConfig, ImageId, new ContainerIdentity(1000, 1000),
            "tenninety-tester-" + new string('0', 32), "/srv/a,b"));
        Assert.Throws<InvalidOperationException>(() => DockerCreateRequest.FromSpec(
            MakeSpec(labels: SandboxAbstractionTests.CompleteLabels(SandboxRole.Tester)
                .Where(kv => kv.Key != "tenninety.run").ToDictionary(kv => kv.Key, kv => kv.Value))
            .ValidateAndCapture(), TestConfig, ImageId, new ContainerIdentity(1000, 1000),
            "tenninety-tester-" + new string('0', 32), "/srv/tenninety/src"));
        Assert.Throws<InvalidOperationException>(() => DockerCreateRequest.FromSpec(
            MakeSpec(environment: new Dictionary<string, string> { ["LD_PRELOAD"] = "/evil.so" })
            .ValidateAndCapture(), TestConfig, ImageId, new ContainerIdentity(1000, 1000),
            "tenninety-tester-" + new string('0', 32), "/srv/tenninety/src"));
    }

    [Fact]
    public void ResolveNetworkName_enforces_the_exact_role_policy_tuple()
    {
        // Valid mappings.
        Assert.Equal("tenninety-coder-model", DockerCreateRequest.ResolveNetworkName(
            SandboxRole.Coder, SandboxNetworkPolicy.Model, TestConfig));
        Assert.Equal("none", DockerCreateRequest.ResolveNetworkName(
            SandboxRole.Reviewer, SandboxNetworkPolicy.None, TestConfig));
        Assert.Equal("none", DockerCreateRequest.ResolveNetworkName(
            SandboxRole.Tester, SandboxNetworkPolicy.None, TestConfig));
        Assert.Equal("tenninety-restore", DockerCreateRequest.ResolveNetworkName(
            SandboxRole.Restore, SandboxNetworkPolicy.Restore, TestConfig));

        // Every invalid role-policy combination fails.
        foreach (var (role, policy) in new[]
                 {
                     (SandboxRole.Coder, SandboxNetworkPolicy.None),
                     (SandboxRole.Coder, SandboxNetworkPolicy.Restore),
                     (SandboxRole.Reviewer, SandboxNetworkPolicy.Model),
                     (SandboxRole.Reviewer, SandboxNetworkPolicy.Restore),
                     (SandboxRole.Tester, SandboxNetworkPolicy.Model),
                     (SandboxRole.Tester, SandboxNetworkPolicy.Restore),
                     (SandboxRole.Restore, SandboxNetworkPolicy.None),
                     (SandboxRole.Restore, SandboxNetworkPolicy.Model),
                 })
        {
            Assert.Throws<InvalidOperationException>(
                () => DockerCreateRequest.ResolveNetworkName(role, policy, TestConfig));
        }
    }

    [Fact]
    public void ResolveNetworkName_rejects_invalid_configured_names_before_any_docker_call()
    {
        foreach (var badName in new[] { "host", "bridge", "none", "default", "", " ", "-bad", "a b" })
        {
            var config = new SandboxConfig { ModelNetwork = badName };
            Assert.Throws<InvalidOperationException>(() => DockerCreateRequest.ResolveNetworkName(
                SandboxRole.Coder, SandboxNetworkPolicy.Model, config));
            var restoreConfig = new SandboxConfig
            {
                Roles = { Tester = { Restore = { NetworkName = badName } } },
            };
            Assert.Throws<InvalidOperationException>(() => DockerCreateRequest.ResolveNetworkName(
                SandboxRole.Restore, SandboxNetworkPolicy.Restore, restoreConfig));
        }
    }

    [Fact]
    public void Factory_resolves_the_network_and_no_string_bypass_exists()
    {
        // A Coder request resolves to the model network.
        var coderRequest = MakeRequest(
            MakeSpec(SandboxRole.Coder, SandboxNetworkPolicy.Model), user: "1000");
        Assert.Equal("tenninety-coder-model", coderRequest.NetworkName);
        // Reviewer/Tester are exactly offline.
        Assert.Equal("none", MakeRequest(MakeSpec(SandboxRole.Reviewer)).NetworkName);
        Assert.Equal("none", MakeRequest(MakeSpec(SandboxRole.Tester)).NetworkName);
        // Restore resolves to the configured restore network.
        var restoreRequest = MakeRequest(
            MakeSpec(SandboxRole.Restore, SandboxNetworkPolicy.Restore), user: "1000");
        Assert.Equal("tenninety-restore", restoreRequest.NetworkName);

        // A Coder request can never carry the restore network: the factory ignores any
        // ambient naming and resolves strictly from the validated tuple — a hostile config
        // that swaps the names still cannot cross the role boundaries.
        var swapped = new SandboxConfig
        {
            ModelNetwork = "tenninety-restore",
            Roles = { Tester = { Restore = { NetworkName = "tenninety-coder-model" } } },
        };
        Assert.Equal("tenninety-restore", DockerCreateRequest.ResolveNetworkName(
            SandboxRole.Coder, SandboxNetworkPolicy.Model, swapped));
        Assert.Equal("tenninety-coder-model", DockerCreateRequest.ResolveNetworkName(
            SandboxRole.Restore, SandboxNetworkPolicy.Restore, swapped));
        // A hostile config naming a reserved network fails before any Docker invocation.
        Assert.Throws<InvalidOperationException>(() => MakeRequest(
            MakeSpec(SandboxRole.Coder, SandboxNetworkPolicy.Model),
            config: new SandboxConfig { ModelNetwork = "host" }));
    }

    [Fact]
    public void CreateRequest_snapshots_labels_and_environment_against_post_validation_mutation()
    {
        var labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Tester);
        var environment = new Dictionary<string, string> { ["TENNINETY_WP"] = "WP-1" };
        var request = MakeRequest(
            MakeSpec(labels: labels, environment: environment));

        // Mutate the ORIGINAL dictionaries after validation — the request must be unaffected.
        labels["tenninety.wp"] = "EVIL";
        labels.Remove("tenninety.run");
        labels["evil.label"] = "x";
        environment["TENNINETY_WP"] = "EVIL";
        environment["LD_PRELOAD"] = "/evil.so";

        Assert.Equal("WP-001", request.Labels["tenninety.wp"]);
        Assert.Equal(SandboxSpec.RequiredLabelKeys.Count, request.Labels.Count);
        Assert.DoesNotContain(request.Labels.Keys, k => k == "evil.label");
        Assert.Equal("WP-1", request.Environment["TENNINETY_WP"]);
        Assert.Single(request.Environment);
        Assert.Throws<InvalidCastException>(() => (Dictionary<string, string>)request.Labels);
    }

    [Fact]
    public void Scope_snapshots_labels_and_rejects_partial_or_unknown_sets()
    {
        var labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
        var scope = DockerContainerScope.FromManagementIdentity(labels);
        labels["tenninety.wp"] = "EVIL";
        labels.Remove("tenninety.run");
        Assert.Equal(SandboxSpec.RequiredLabelKeys.Count, scope.Labels.Count);
        Assert.True(scope.Labels.ContainsKey("tenninety.run"));
        Assert.Throws<InvalidCastException>(() => (Dictionary<string, string>)scope.Labels);

        Assert.Throws<InvalidOperationException>(() =>
            DockerContainerScope.FromManagementIdentity(new Dictionary<string, string>()));
        Assert.Throws<InvalidOperationException>(() =>
            DockerContainerScope.FromManagementIdentity(new Dictionary<string, string>
            {
                ["tenninety.instance"] = "i", ["tenninety.repository"] = "r", ["tenninety.run"] = "run",
            }));
        Assert.Throws<InvalidOperationException>(() =>
            DockerContainerScope.FromManagementIdentity(new Dictionary<string, string>
            {
                ["tenninety.instance"] = "i", ["tenninety.repository"] = "r",
                ["tenninety.run"] = "run", ["tenninety.wp"] = "w",
                ["tenninety.attempt"] = "1", ["tenninety.role"] = "coder",
                ["evil.label"] = "x",
            }));
    }

    [Fact]
    public void ExecRequest_revalidates_every_command_invariant_before_existing()
    {
        Assert.Throws<InvalidOperationException>(() =>
            DockerExecRequest.FromCommand("bad-id", MakeCommand(), TimeSpan.FromMinutes(1)));
        Assert.Throws<InvalidOperationException>(() =>
            DockerExecRequest.FromCommand(ContainerId, MakeCommand("a\0b"), TimeSpan.FromMinutes(1)));
        Assert.Throws<InvalidOperationException>(() =>
            DockerExecRequest.FromCommand(ContainerId,
                MakeCommand(workingDirectory: "/etc"), TimeSpan.FromMinutes(1)));
        Assert.Throws<InvalidOperationException>(() =>
            DockerExecRequest.FromCommand(ContainerId, MakeCommand(), TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() =>
            DockerExecRequest.FromCommand(ContainerId,
                MakeCommand(maxOutputBytes: 0), TimeSpan.FromMinutes(1)));
        Assert.Throws<InvalidOperationException>(() =>
            DockerExecRequest.FromCommand(ContainerId,
                MakeCommand(stdIn: new string('\u20ac', 600_000)), // 1.8 MB UTF-8 > 1 MiB cap
                TimeSpan.FromMinutes(1)));
        Assert.Throws<InvalidOperationException>(() =>
            DockerExecRequest.FromCommand(ContainerId,
                MakeCommand(environment: new Dictionary<string, string> { ["LD_PRELOAD"] = "/evil.so" }),
                TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ExecRequest_snapshots_command_data_against_post_validation_mutation()
    {
        var arguments = new List<string> { "test", "-c", "Release" };
        var environment = new Dictionary<string, string> { ["TENNINETY_WP"] = "WP-1" };
        var command = MakeCommand("dotnet", arguments: arguments, environment: environment);
        var request = DockerExecRequest.FromCommand(ContainerId, command, TimeSpan.FromMinutes(1));

        arguments.Add("--evil");
        arguments[0] = "sh";
        environment["TENNINETY_WP"] = "EVIL";
        environment["LD_PRELOAD"] = "/evil.so";

        Assert.Equal(["test", "-c", "Release"], request.Arguments);
        Assert.Equal("WP-1", request.Environment["TENNINETY_WP"]);
        Assert.Single(request.Environment);
    }

    // ---- version / info ------------------------------------------------------

    [Fact]
    public async Task GetVersion_uses_the_exact_vector_and_parses_the_real_shape()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(
            "{\"Client\":{\"Version\":\"1.0\",\"Os\":\"linux\",\"Arch\":\"amd64\"}," +
            "\"Server\":{\"Version\":\"29.0.7\",\"Os\":\"linux\",\"Arch\":\"amd64\"}}"));
        var cli = new DockerCli(transport);

        var info = await cli.GetVersionAsync();

        Assert.Equal(["version", "--format", "{{json .}}"], transport.Invocations[0].Arguments);
        Assert.Equal("29.0.7", info.ServerVersion);
        Assert.Equal("linux", info.OsType);
        Assert.Equal("amd64", info.Architecture);
    }

    [Fact]
    public async Task GetVersion_without_a_Server_object_fails_closed()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok("{\"Client\":{\"Version\":\"1.0\"}}"));
        var cli = new DockerCli(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(() => cli.GetVersionAsync());
    }

    [Fact]
    public async Task GetInfo_uses_the_exact_vector_and_parses_daemon_facts()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(
            "{\"ServerVersion\":\"29.0.7\",\"OSType\":\"linux\",\"Architecture\":\"amd64\"," +
            "\"CgroupVersion\":\"2\",\"CgroupDriver\":\"systemd\"," +
            "\"SecurityOptions\":[\"name=seccomp,profile=default\",\"name=apparmor\",\"name=rootless\"]}"));
        var cli = new DockerCli(transport);

        var info = await cli.GetInfoAsync();

        Assert.Equal(["info", "--format", "{{json .}}"], transport.Invocations[0].Arguments);
        Assert.Equal("2", info.CgroupVersion);
        Assert.Equal("systemd", info.CgroupDriver);
        Assert.True(info.CgroupEnforcementReliable);
        Assert.True(info.HasSeccomp);
        Assert.True(info.HasAppArmor);
        Assert.False(info.HasSelinux);
        Assert.True(info.Rootless);
    }

    // ---- image inspect + digest evidence --------------------------------------

    [Fact]
    public async Task InspectImage_uses_the_exact_vector_and_parses_all_digests_and_entrypoint()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson(ImageId, "1000:1000",
            ["repo.example.com/team/img@sha256:" + new string('c', 64),
             "other.example.com/x@sha256:" + new string('d', 64)])));
        var cli = new DockerCli(transport);

        var info = await cli.InspectImageAsync(ImageId);

        Assert.Equal(["image", "inspect", ImageId], transport.Invocations[0].Arguments);
        Assert.Equal(ImageId, info.ImageId);
        Assert.Equal(2, info.RepoDigests.Count);
        Assert.Equal("1000:1000", info.ConfiguredUser);
        Assert.Empty(info.ConfigEntrypoint);
    }

    [Fact]
    public async Task Exact_local_image_id_requires_an_exact_inspected_match()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson(OtherImageId)));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectImageAsync(ImageId));
        Assert.Contains("does not match", ex.Message);
        Assert.Contains("never pulled", ex.Message);
    }

    [Fact]
    public async Task Registry_digest_success_uses_matching_evidence()
    {
        var digest = new string('c', 64);
        var reference = "registry.example.com/team/img@sha256:" + digest;
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson(ImageId, repoDigests:
            ["registry.example.com/team/img@sha256:" + digest,
             "other.example.com/x@sha256:" + new string('d', 64)])));
        var cli = new DockerCli(transport);

        var info = await cli.InspectImageAsync(reference);

        Assert.Equal(["image", "inspect", reference], transport.Invocations[0].Arguments);
        Assert.Equal(ImageId, info.ImageId);
    }

    [Fact]
    public async Task Registry_digest_with_no_evidence_fails_closed()
    {
        var digest = new string('c', 64);
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson(ImageId, repoDigests: [])));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectImageAsync("registry.example.com/team/img@sha256:" + digest));
        Assert.Contains("no repository-digest evidence", ex.Message);
    }

    [Fact]
    public async Task Registry_digest_with_a_different_repository_fails_closed()
    {
        var digest = new string('c', 64);
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson(ImageId, repoDigests:
            ["other.example.com/x@sha256:" + digest])));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectImageAsync("registry.example.com/team/img@sha256:" + digest));
        Assert.Contains("no repository-digest evidence", ex.Message);
    }

    [Fact]
    public async Task Registry_digest_mismatch_fails_closed()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson(ImageId, repoDigests:
            ["registry.example.com/team/img@sha256:" + new string('e', 64)])));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectImageAsync("registry.example.com/team/img@sha256:" + new string('c', 64)));
        Assert.Contains("does not match the pinned digest", ex.Message);
    }

    [Fact]
    public async Task Contradictory_digest_evidence_fails_closed()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson(ImageId, repoDigests:
            ["registry.example.com/team/img@sha256:" + new string('c', 64),
             "registry.example.com/team/img@sha256:" + new string('e', 64)])));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectImageAsync("registry.example.com/team/img@sha256:" + new string('c', 64)));
        Assert.Contains("contradictory repository-digest evidence", ex.Message);
    }

    [Fact]
    public async Task Unpinned_or_malformed_image_references_are_rejected_without_a_call()
    {
        var transport = new FakeDockerCliTransport();
        var cli = new DockerCli(transport);
        foreach (var reference in new[] { "ubuntu:latest", "ubuntu", "", "sha256:short", "a b" })
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cli.InspectImageAsync(reference));
        Assert.Empty(transport.Invocations);
    }

    [Fact]
    public async Task Malformed_inspected_image_id_fails_closed()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ImageInspectJson("sha256:notaheximageid")));
        var cli = new DockerCli(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectImageAsync(ImageId));
    }

    [Fact]
    public async Task Malformed_entrypoint_shapes_fail_closed()
    {
        // Present but not an array; non-string member; blank member; NUL member.
        foreach (var entrypointJson in new[]
                 {
                     "\"/entrypoint.sh\"",
                     "[1]",
                     "[\"\"]",
                     "[\"a\\0b\"]",
                 })
        {
            var transport = new FakeDockerCliTransport();
            transport.Enqueue(Ok(
                "[{\"Id\":\"" + ImageId + "\",\"RepoDigests\":[],\"Config\":{\"User\":\"1000:1000\"," +
                "\"Entrypoint\":" + entrypointJson + "}}]"));
            var cli = new DockerCli(transport);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cli.InspectImageAsync(ImageId));
        }
    }

    // ---- network inspect ---------------------------------------------------------

    [Fact]
    public async Task InspectNetwork_uses_the_exact_vector_and_requires_the_requested_name()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok("[{\"Name\":\"tenninety-coder-model\",\"Id\":\"net-1\",\"Driver\":\"bridge\"}]"));
        var cli = new DockerCli(transport);

        var info = await cli.InspectNetworkAsync("tenninety-coder-model");

        Assert.Equal(["network", "inspect", "tenninety-coder-model"], transport.Invocations[0].Arguments);
        Assert.NotNull(info);
        Assert.Equal("tenninety-coder-model", info!.Name);
        Assert.False(info.IsReserved);
    }

    [Fact]
    public async Task InspectNetwork_returns_null_only_on_positively_established_absence()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err("Error: No such network: tenninety-coder-model"));
        var cli = new DockerCli(transport);
        Assert.Null(await cli.InspectNetworkAsync("tenninety-coder-model"));
    }

    [Fact]
    public async Task InspectNetwork_recognizes_modern_daemon_not_found_phrasing_as_absence()
    {
        // Docker >= 29 reports a missing network as
        // "Error response from daemon: network <name> not found" — observed live.
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err("Error response from daemon: network tenninety-coder-model not found"));
        var cli = new DockerCli(transport);
        Assert.Null(await cli.InspectNetworkAsync("tenninety-coder-model"));
    }

    [Fact]
    public async Task InspectNetwork_does_not_treat_an_unrelated_failure_mentioning_a_network_as_absence()
    {
        // "network" + "not found" must both be absent-yet-realistic daemon phrasings; a
        // completely different failure that never mentions "not found" is operational.
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err("Error response from daemon: network configuration is broken"));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectNetworkAsync("tenninety-coder-model"));
        Assert.Contains("docker network inspect failed", ex.Message);
    }

    [Fact]
    public async Task InspectNetwork_preserves_operational_failures_instead_of_absence()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(TimedOutErr());
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectNetworkAsync("tenninety-coder-model"));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task InspectNetwork_rejects_a_mismatched_inspected_name()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok("[{\"Name\":\"some-other-network\",\"Id\":\"net-1\",\"Driver\":\"bridge\"}]"));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectNetworkAsync("tenninety-coder-model"));
        Assert.Contains("mismatched identity", ex.Message);
    }

    [Fact]
    public async Task InspectNetwork_rejects_malformed_and_reserved_names_without_a_call()
    {
        var transport = new FakeDockerCliTransport();
        var cli = new DockerCli(transport);
        foreach (var name in new[]
                 {
                     "", " ", "host", "bridge", "none", "default", "HOST", "-leading-dash",
                     "with space", "with\nnewline", "-x", "a\0b",
                 })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cli.InspectNetworkAsync(name));
        }
        Assert.Empty(transport.Invocations);
    }

    // ---- typed create -------------------------------------------------------------

    [Fact]
    public async Task Typed_create_builds_the_complete_exact_argument_vector()
    {
        var workspaceSource = Path.Combine(_managedRoot.Root, "attempt 1", "source");
        Directory.CreateDirectory(workspaceSource);
        var request = MakeRequest(
            spec: MakeSpec(),
            source: workspaceSource,
            containerName: "tenninety-tester-" + new string('0', 32));
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerId));
        var cli = new DockerCli(transport);

        var id = await cli.CreateContainerAsync(request);

        Assert.Equal(ContainerId, id);
        var actual = transport.Invocations[0].Arguments;
        var expected = new List<string>
        {
            "create",
            "--name", "tenninety-tester-" + new string('0', 32),
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
            "--mount", $"type=bind,source={workspaceSource},target=/workspace,bind-propagation=rprivate",
            request.ExactImageId,
            "sleep",
            "infinity",
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task DockerCli_create_boundary_rejects_a_corrupted_request_without_calling_docker()
    {
        // The request type is immutable, so a hostile value can only appear at the adapter
        // boundary through reflection-level tampering — which the boundary re-checks.
        var request = MakeRequest();
        var tampered = (DockerCreateRequest)RuntimeHelpersGetUninitialized(request);
        var transport = new FakeDockerCliTransport();
        var cli = new DockerCli(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.CreateContainerAsync(tampered));
        Assert.Empty(transport.Invocations);
    }

    private static DockerCreateRequest RuntimeHelpersGetUninitialized(DockerCreateRequest request)
    {
        // Build a request whose internal fields are corrupted via reflection to prove the
        // adapter boundary re-validates instead of trusting the request object.
        var clone = (DockerCreateRequest)System.Runtime.CompilerServices
            .RuntimeHelpers.GetUninitializedObject(typeof(DockerCreateRequest));
        foreach (var field in typeof(DockerCreateRequest)
                     .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            field.SetValue(clone, field.GetValue(request));
        typeof(DockerCreateRequest)
            .GetField("<NetworkName>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(clone, "host");
        return clone;
    }

    [Fact]
    public async Task Create_failure_messages_are_bounded_and_scrub_the_workspace_source()
    {
        var workspaceSource = Path.Combine(_managedRoot.Root, "attempt-scrub");
        Directory.CreateDirectory(workspaceSource);
        var request = MakeRequest(source: workspaceSource);
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err($"error: mount source {workspaceSource} is invalid"));
        var cli = new DockerCli(transport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.CreateContainerAsync(request));

        Assert.DoesNotContain(request.WorkspaceSource, ex.Message);
        Assert.Contains("[workspace]", ex.Message);
        Assert.True(ex.Message.Length < 1024);
    }

    // ---- start / inspect / stop / kill / remove -------------------------------------

    [Fact]
    public async Task Start_uses_the_exact_vector_and_validates_the_container_id()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok());
        var cli = new DockerCli(transport);
        await cli.StartContainerAsync(ContainerId);
        Assert.Equal(["start", ContainerId], transport.Invocations[0].Arguments);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.StartContainerAsync("short-id"));
        Assert.Single(transport.Invocations);
    }

    [Fact]
    public async Task InspectContainer_uses_the_exact_vector_and_validates_identity()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerInspectJson(ContainerId, ImageId)));
        var cli = new DockerCli(transport);

        var state = await cli.InspectContainerAsync(ContainerId, expectedImageId: ImageId);

        Assert.Equal(["inspect", ContainerId], transport.Invocations[0].Arguments);
        Assert.True(state.Running);
        Assert.False(state.OomKilled);
    }

    [Fact]
    public async Task InspectContainer_rejects_a_returned_id_that_differs_from_the_request()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerInspectJson(new string('2', 64), ImageId)));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectContainerAsync(ContainerId, expectedImageId: ImageId));
        Assert.Contains("mismatched identity", ex.Message);
    }

    [Fact]
    public async Task InspectContainer_rejects_an_image_mismatch()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerInspectJson(ContainerId, OtherImageId)));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectContainerAsync(ContainerId, expectedImageId: ImageId));
        Assert.Contains("does not match the resolved exact image id", ex.Message);
    }

    [Fact]
    public async Task InspectContainer_rejects_malformed_inspected_identifiers()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok("[{\"Id\":\"garbage\",\"Image\":\"sha256:x\",\"State\":{\"Running\":true}}]"));
        var cli = new DockerCli(transport);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectContainerAsync(ContainerId));
    }

    [Fact]
    public async Task TryInspect_returns_null_only_on_positively_established_absence()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err("Error: No such object: " + ContainerId));
        var cli = new DockerCli(transport);
        Assert.Null(await cli.TryInspectContainerAsync(ContainerId));
    }

    [Fact]
    public async Task TryInspect_recognizes_modern_lowercase_absence_phrasing()
    {
        // Docker >= 29 prints absence as lowercase "error: no such object: <id>" — observed
        // live after `docker rm` reported success.
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err("error: no such object: " + ContainerId));
        var cli = new DockerCli(transport);
        Assert.Null(await cli.TryInspectContainerAsync(ContainerId));
    }

    [Fact]
    public async Task Remove_accepts_lowercase_absence_confirmation_after_successful_rm()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerId));                                     // rm
        transport.Enqueue(Err("error: no such object: " + ContainerId));        // absence proof
        var cli = new DockerCli(transport);

        var removed = await cli.RemoveContainerAsync(ContainerId);

        Assert.True(removed);
        Assert.Equal(2, transport.Invocations.Count);
    }

    [Fact]
    public async Task TryInspect_preserves_timeout_instead_of_absence()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(TimedOutErr());
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.TryInspectContainerAsync(ContainerId));
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Duplicate_json_fields_are_rejected_as_hostile()
    {
        var transport = new FakeDockerCliTransport();
        var duplicated = "[{\"Id\":\"" + ContainerId + "\",\"Id\":\"" + ContainerId + "\"," +
                         "\"Image\":\"" + ImageId + "\",\"State\":{\"Running\":true}}]";
        transport.Enqueue(Ok(duplicated));
        var cli = new DockerCli(transport);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.InspectContainerAsync(ContainerId));
        Assert.Contains("duplicate field", ex.Message);
    }

    [Fact]
    public async Task Stop_uses_the_exact_vector()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok());
        var cli = new DockerCli(transport);
        await cli.StopContainerAsync(ContainerId, TimeSpan.FromSeconds(10));
        Assert.Equal(["stop", "--time", "10", ContainerId], transport.Invocations[0].Arguments);
    }

    [Fact]
    public async Task Kill_uses_the_exact_vector()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok());
        var cli = new DockerCli(transport);
        await cli.KillContainerAsync(ContainerId);
        Assert.Equal(["kill", ContainerId], transport.Invocations[0].Arguments);
    }

    // ---- removal with REQUIRED final absence proof --------------------------------

    [Fact]
    public async Task Remove_success_is_confirmed_by_absence_and_returns_true()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerId));                                       // rm
        transport.Enqueue(Err("Error: No such object: " + ContainerId));           // absence proof
        var cli = new DockerCli(transport);

        var removed = await cli.RemoveContainerAsync(ContainerId);

        Assert.True(removed);
        Assert.Equal(["rm", "--force", ContainerId], transport.Invocations[0].Arguments);
        Assert.Equal(["inspect", ContainerId], transport.Invocations[1].Arguments);
    }

    [Fact]
    public async Task Remove_success_followed_by_a_still_present_container_is_a_contradiction()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerId));                                // rm "succeeds"
        transport.Enqueue(Ok(ContainerInspectJson(ContainerId, ImageId))); // still present!
        var cli = new DockerCli(transport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.RemoveContainerAsync(ContainerId));

        Assert.Contains("contradiction", ex.Message);
        Assert.Contains("unproven", ex.Message);
    }

    [Fact]
    public async Task Remove_no_such_response_confirmed_by_absence_returns_false()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err("Error: No such container: " + ContainerId));  // rm
        transport.Enqueue(Err("Error: No such object: " + ContainerId));     // absence proof
        var cli = new DockerCli(transport);

        var removed = await cli.RemoveContainerAsync(ContainerId);

        Assert.False(removed);
        Assert.Equal(2, transport.Invocations.Count);
    }

    [Fact]
    public async Task Remove_no_such_response_followed_by_a_present_container_throws()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Err("Error: No such container: " + ContainerId));  // rm claims absent
        transport.Enqueue(Ok(ContainerInspectJson(ContainerId, ImageId)));   // inspect: present!
        var cli = new DockerCli(transport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.RemoveContainerAsync(ContainerId));

        Assert.Contains("contradiction", ex.Message);
    }

    [Fact]
    public async Task Remove_confirmation_inspect_timeout_throws_instead_of_claiming_success()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerId));   // rm succeeds
        transport.Enqueue(TimedOutErr());     // confirmation inspect times out
        var cli = new DockerCli(transport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.RemoveContainerAsync(ContainerId));

        Assert.Contains("confirming inspect failed", ex.Message);
        Assert.Contains("timed out", ex.Message);
    }

    [Fact]
    public async Task Remove_confirmation_inspect_malformed_output_throws()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerId));        // rm succeeds
        transport.Enqueue(Ok("[{\"Id\":\"garbage\"}")); // malformed confirmation
        var cli = new DockerCli(transport);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.RemoveContainerAsync(ContainerId));
    }

    // ---- labelled list with strict ID validation -----------------------------------

    [Fact]
    public async Task Labelled_list_uses_the_exact_vector_with_sorted_complete_identity()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok(ContainerId + "\n" + new string('2', 64) + "\n"));
        var cli = new DockerCli(transport);

        var scope = DockerContainerScope.FromManagementIdentity(
            SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder));
        var ids = await cli.ListContainersAsync(scope);

        Assert.Equal(2, ids.Count);
        var expected = new List<string>
        {
            "ps", "--all", "--no-trunc", "--quiet",
            "--filter", "label=tenninety.attempt=1",
            "--filter", "label=tenninety.instance=test-instance",
            "--filter", "label=tenninety.repository=demo-repository",
            "--filter", "label=tenninety.role=coder",
            "--filter", "label=tenninety.run=run-0001",
            "--filter", "label=tenninety.wp=WP-001",
            "--format", "{{.ID}}",
        };
        Assert.Equal(expected, transport.Invocations[0].Arguments);
    }

    [Fact]
    public async Task Labelled_list_rejects_malformed_partial_and_duplicate_ids()
    {
        var cli = Scripted(
            Ok("shortid\n"),                       // partial/malformed
            Ok(ContainerId + "\n" + ContainerId),  // duplicate
            Ok("-option-like-not-hex\n"),          // option-like
            Ok(ContainerId + "\ngarbage extra\n")); // garbage

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.ListContainersAsync(DockerContainerScope.FromManagementIdentity(
                SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder))));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.ListContainersAsync(DockerContainerScope.FromManagementIdentity(
                SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder))));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.ListContainersAsync(DockerContainerScope.FromManagementIdentity(
                SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder))));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => cli.ListContainersAsync(DockerContainerScope.FromManagementIdentity(
                SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder))));
    }

    // ---- typed exec ---------------------------------------------------------------

    [Fact]
    public async Task Typed_exec_builds_the_complete_exact_argument_vector()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok("done"));
        var cli = new DockerCli(transport);

        var command = new SandboxCommand
        {
            Executable = "dotnet",
            Arguments = ["test", "a b $(x) | ;"],
            WorkingDirectory = "/workspace/sub dir",
            StdIn = "payload",
            Timeout = TimeSpan.FromMinutes(1),
            MaxOutputBytes = 4096,
            Environment = new Dictionary<string, string>
            {
                ["DOTNET_NOLOGO"] = "1",
                ["TENNINETY_WP"] = "WP-1",
                ["TENNINETY_ATTEMPT"] = "2",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
        };
        var result = await cli.ExecAsync(
            DockerExecRequest.FromCommand(ContainerId, command, command.Timeout ?? TimeSpan.FromMinutes(1)));

        Assert.True(result.Succeeded);
        var expected = new List<string>
        {
            "exec",
            "--interactive",
            "--workdir", "/workspace/sub dir",
            "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "--env", "DOTNET_NOLOGO=1",
            "--env", "TENNINETY_ATTEMPT=2",
            "--env", "TENNINETY_WP=WP-1",
            ContainerId,
            "dotnet",
            "test",
            "a b $(x) | ;",
        };
        Assert.Equal(expected, transport.Invocations[0].Arguments);
        Assert.Equal("payload", transport.Invocations[0].StdIn);
    }

    [Fact]
    public async Task Typed_exec_without_stdin_omits_the_interactive_flag()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(Ok());
        var cli = new DockerCli(transport);
        var command = new SandboxCommand
        {
            Executable = "true",
            Arguments = [],
            WorkingDirectory = "/workspace",
            Timeout = TimeSpan.FromMinutes(1),
            MaxOutputBytes = 4096,
            Environment = new Dictionary<string, string>(),
        };
        await cli.ExecAsync(DockerExecRequest.FromCommand(ContainerId, command, command.Timeout ?? TimeSpan.FromMinutes(1)));
        Assert.DoesNotContain("--interactive", transport.Invocations[0].Arguments);
        Assert.Null(transport.Invocations[0].StdIn);
    }

    [Fact]
    public async Task Exec_maps_timeout_cancellation_and_truncation_flags()
    {
        var cli = Scripted(
            new DockerCliResult(-1, "", "", TimedOut: true, Cancelled: false,
                OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)),
            new DockerCliResult(-1, "", "", TimedOut: false, Cancelled: true,
                OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)),
            new DockerCliResult(-1, "", "", TimedOut: false, Cancelled: false,
                OutputTruncated: true, Duration: TimeSpan.FromMilliseconds(1)));

        var timedOut = await cli.ExecAsync(DockerExecRequest.FromCommand(
            ContainerId, MakeCommand(), TimeSpan.FromMinutes(1)));
        Assert.True(timedOut.TimedOut);
        Assert.False(timedOut.Succeeded);

        var cancelled = await cli.ExecAsync(DockerExecRequest.FromCommand(
            ContainerId, MakeCommand(), TimeSpan.FromMinutes(1)));
        Assert.True(cancelled.Cancelled);
        Assert.False(cancelled.Succeeded);

        var truncated = await cli.ExecAsync(DockerExecRequest.FromCommand(
            ContainerId, MakeCommand(), TimeSpan.FromMinutes(1)));
        Assert.True(truncated.OutputTruncated);
        Assert.False(truncated.Succeeded);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static DockerCli Scripted(params DockerCliResult[] results)
    {
        var transport = new FakeDockerCliTransport();
        foreach (var result in results) transport.Enqueue(result);
        return new DockerCli(transport);
    }
}
