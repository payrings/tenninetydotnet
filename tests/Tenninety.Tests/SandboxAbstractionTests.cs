using System.Collections.ObjectModel;
using System.Text.Json;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// In-memory Docker CLI transport: records every typed invocation argument vector it receives
/// and returns scripted results. Used to prove that Docker argument construction stays fully
/// typed — an executable plus an exact argument vector, never a joined shell string — and to
/// prove mock mode performs zero Docker calls.
/// </summary>
public sealed class FakeDockerCliTransport : IDockerCliTransport
{
    public readonly List<DockerCliInvocation> Invocations = new();
    private readonly Queue<DockerCliResult> _scripted = new();

    public int CallCount => Invocations.Count;

    public void Enqueue(DockerCliResult result) => _scripted.Enqueue(result);

    public Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default)
    {
        Invocations.Add(invocation);
        return Task.FromResult(_scripted.Count > 0
            ? _scripted.Dequeue()
            : new DockerCliResult(0, "", "", TimedOut: false, Cancelled: false,
                OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)));
    }

    /// <summary>
    /// Asserts that the transport was never asked to run a host shell: the first recorded
    /// argument element must never be a shell binary. (A shell may of course appear as an
    /// ARGUMENT for an in-container exec — that is the sandbox Tester contract — but the host
    /// process the transport represents is always the Docker CLI, never a shell.)
    /// </summary>
    public void AssertNoHostShellInvocation()
    {
        Assert.NotEmpty(Invocations);
        Assert.All(Invocations, inv =>
        {
            Assert.True(inv.Arguments.Count > 0, "an invocation must carry at least one argument element.");
            Assert.DoesNotContain(inv.Arguments[0], HostShellBinaries);
        });
    }

    private static readonly string[] HostShellBinaries =
    [
        "sh", "bash", "dash", "ash", "zsh", "ksh",
        "/bin/sh", "/bin/bash", "/bin/dash", "/bin/ash", "/bin/zsh",
        "/usr/bin/sh", "/usr/bin/bash", "/usr/bin/zsh",
    ];
}

/// <summary>
/// Minimal in-memory <see cref="ISandboxSession"/> implementing the sanitized agent-facing
/// contract: it exposes <see cref="ISandboxSession.Info"/> only (session ID, role, state,
/// /workspace path) and never holds a spec or host path. Quiescence is only reachable via
/// StopAsync.
/// </summary>
public sealed class FakeSandboxSession(SandboxRole role) : ISandboxSession
{
    public string ContainerId { get; } = $"fake-{Guid.NewGuid():N}";
    private SandboxSessionState _state = SandboxSessionState.Created;

    public SandboxSessionInfo Info =>
        new(ContainerId, role, _state);

    public bool WritesQuiescent => _state == SandboxSessionState.StoppedQuiescent;

    public readonly List<SandboxCommand> Executed = new();
    public bool Disposed { get; private set; }

    public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken ct = default)
    {
        if (_state is not (SandboxSessionState.Created or SandboxSessionState.Running))
            throw new InvalidOperationException("session is no longer running.");
        Executed.Add(command);
        _state = SandboxSessionState.Running;
        return Task.FromResult(new SandboxCommandResult(
            ExitCode: 0, StdOutTail: "", StdErrTail: "",
            TimedOut: false, Cancelled: false, OomKilled: false,
            OutputTruncated: false, Duration: TimeSpan.Zero));
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _state = SandboxSessionState.StoppedQuiescent;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Phase 1 tests for the inert sandbox/candidate abstractions.</summary>
public class SandboxAbstractionTests : IDisposable
{
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private string NewWorkspaceDir()
    {
        var dir = Directory.CreateDirectory(Path.Combine(
            _managedRoot.Root, "attempt-" + Guid.NewGuid().ToString("N")));
        return dir.FullName;
    }

    internal static string RoleName(SandboxRole role) => role.ToString().ToLowerInvariant();

    /// <summary>Complete, internally consistent management identity labels.</summary>
    internal static Dictionary<string, string> CompleteLabels(
        SandboxRole role, string? candidateSha = null)
    {
        var labels = new Dictionary<string, string>
        {
            ["tenninety.instance"] = "test-instance",
            ["tenninety.repository"] = "demo-repository",
            ["tenninety.run"] = "run-0001",
            ["tenninety.wp"] = "WP-001",
            ["tenninety.attempt"] = "1",
            ["tenninety.role"] = RoleName(role),
        };
        if (candidateSha is not null) labels["tenninety.candidate"] = candidateSha;
        return labels;
    }

    private sealed class SpecDraft
    {
        public SandboxRole Role = SandboxRole.Coder;
        public string Image = "sha256:" + new string('a', 64);
        public SandboxNetworkPolicy Network = SandboxNetworkPolicy.Model;
        public double Cpus = 4.0;
        public int MemoryMb = 8192;
        public int Pids = 256;
        public TimeSpan Timeout = TimeSpan.FromMinutes(30);
        public string? CandidateSha;
        public bool OmitWorkspace;
        public Dictionary<string, string>? Labels;
    }

    private SandboxSpec ValidSpec(Action<SpecDraft>? mutate = null)
    {
        var draft = new SpecDraft();
        mutate?.Invoke(draft);
        return new SandboxSpec
        {
            Role = draft.Role,
            Image = draft.Image,
            HostWorkspacePath = draft.OmitWorkspace
                ? null
                : ValidatedSandboxWorkspacePath.Create(
                    NewWorkspaceDir(), _managedRoot.Root, _repo.Root),
            Network = draft.Network,
            Cpus = draft.Cpus,
            MemoryMb = draft.MemoryMb,
            Pids = draft.Pids,
            Timeout = draft.Timeout,
            Labels = draft.Labels ?? CompleteLabels(draft.Role, draft.CandidateSha),
            Environment = new Dictionary<string, string> { ["TENNINETY_WP"] = "WP-001" },
            CandidateSha = draft.CandidateSha,
        };
    }

    private SandboxSpec BuildSpec(
        SandboxRole role = SandboxRole.Coder,
        IReadOnlyDictionary<string, string>? env = null,
        IReadOnlyDictionary<string, string>? labels = null,
        bool omitWorkspace = false,
        string? candidateSha = null) =>
        new()
        {
            Role = role,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = omitWorkspace
                ? null
                : ValidatedSandboxWorkspacePath.Create(
                    NewWorkspaceDir(), _managedRoot.Root, _repo.Root),
            Network = role switch
            {
                SandboxRole.Coder => SandboxNetworkPolicy.Model,
                SandboxRole.Reviewer or SandboxRole.Tester => SandboxNetworkPolicy.None,
                SandboxRole.Restore => SandboxNetworkPolicy.Restore,
                _ => throw new ArgumentOutOfRangeException(nameof(role)),
            },
            Cpus = 4.0,
            MemoryMb = 8192,
            Pids = 256,
            Timeout = TimeSpan.FromMinutes(30),
            Labels = labels ?? CompleteLabels(role, candidateSha),
            Environment = env ?? new Dictionary<string, string>(),
            CandidateSha = candidateSha,
        };

    // ---- spec -------------------------------------------------------------------

    [Fact]
    public void Workspace_mount_path_is_fixed_by_policy()
    {
        var spec = ValidSpec();
        Assert.Equal("/workspace", spec.ContainerWorkspacePath);
        Assert.Equal("/workspace", SandboxPolicy.ContainerWorkspacePath);
        // No settable member can point the mount anywhere else.
        Assert.DoesNotContain(typeof(SandboxSpec).GetProperties(),
            p => p.Name == "ContainerWorkspacePath" && p.CanWrite);
    }

    [Fact]
    public void Root_filesystem_is_read_only_by_policy()
    {
        Assert.True(ValidSpec().ReadOnlyRootFileSystem);
        Assert.True(SandboxPolicy.ReadOnlyRootFileSystem);
    }

    [Fact]
    public void Tmpfs_mounts_are_bounded_and_fixed()
    {
        var mounts = ValidSpec().TmpfsMounts;
        Assert.Contains(mounts, m => m.ContainerPath == "/tmp");
        Assert.Contains(mounts, m => m.ContainerPath == SandboxPolicy.ContainerHomePath);
        Assert.All(mounts, m => Assert.Contains("size=", m.Options));
    }

    [Fact]
    public void Network_policy_is_a_closed_enum_not_an_arbitrary_string()
    {
        // The spec physically cannot carry an arbitrary Docker network name.
        Assert.Equal(typeof(SandboxNetworkPolicy),
            typeof(SandboxSpec).GetProperty("Network")!.PropertyType);
        Assert.Equal(
            new[] { SandboxNetworkPolicy.None, SandboxNetworkPolicy.Model, SandboxNetworkPolicy.Restore },
            Enum.GetValues<SandboxNetworkPolicy>());
        ValidSpec().Validate();
    }

    [Fact]
    public void Reviewer_and_tester_specs_require_no_network()
    {
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d =>
        {
            d.Role = SandboxRole.Reviewer;
            d.Network = SandboxNetworkPolicy.Model;
        }).Validate());
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d =>
        {
            d.Role = SandboxRole.Tester;
            d.Network = SandboxNetworkPolicy.Model;
        }).Validate());

        ValidSpec(d =>
        {
            d.Role = SandboxRole.Reviewer;
            d.Network = SandboxNetworkPolicy.None;
        }).Validate();
        ValidSpec(d =>
        {
            d.Role = SandboxRole.Tester;
            d.Network = SandboxNetworkPolicy.None;
        }).Validate();
    }

    [Fact]
    public void Coder_spec_requires_model_network_and_restore_requires_restore_network()
    {
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d =>
            d.Network = SandboxNetworkPolicy.None).Validate());
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d =>
            d.Network = SandboxNetworkPolicy.Restore).Validate());
        ValidSpec(d => d.Network = SandboxNetworkPolicy.Model).Validate();
        ValidSpec(d =>
        {
            d.Role = SandboxRole.Restore;
            d.Network = SandboxNetworkPolicy.Restore;
        }).Validate();
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d =>
        {
            d.Role = SandboxRole.Restore;
            d.Network = SandboxNetworkPolicy.None;
        }).Validate());
    }

    [Fact]
    public void Valid_spec_passes_validation() => ValidSpec().Validate();

    // ---- pinned images (Blocker 2) ----------------------------------------------

    [Fact]
    public void Spec_rejects_unpinned_images()
    {
        var badImages = new[]
        {
            "ubuntu:latest",
            "ubuntu",
            "",
            "   ",
            "\t",
            "sha256:" + new string('a', 63),            // incomplete digest
            "sha256:" + new string('A', 64),            // uppercase hex
            "sha256:" + new string('g', 64),            // non-hex
            "sha256:",                                   // empty digest
            "tenninety/coder@sha256:" + new string('a', 63),
            "tenninety/coder@sha256:" + "zz",
            "tenninety/coder@md5:" + new string('a', 32),
            "tenninety/coder@",
            "tenninety/coder",
            "tenninety/coder:latest@sha256:" + new string('a', 64) + "extra",
            "tenninety/coder@sha256:" + new string('a', 64) + " ",
        };
        foreach (var image in badImages)
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => ValidSpec(d => d.Image = image).Validate());
            Assert.Contains("unpinned image", ex.Message);
        }
    }

    [Fact]
    public void Spec_accepts_only_pinned_images()
    {
        // Exact lowercase sha256: local image ID.
        ValidSpec(d => d.Image = "sha256:" + new string('a', 64)).Validate();
        // Registry reference with @sha256: and exactly 64 lowercase hex digits.
        ValidSpec(d => d.Image = "ghcr.io/tenninety/coder-aider@sha256:" + new string('b', 64))
            .Validate();
        ValidSpec(d => d.Image = "registry.example.com:5000/team/coder@sha256:" + new string('c', 64))
            .Validate();
    }

    // ---- spec limits ------------------------------------------------------------

    [Fact]
    public void Spec_rejects_invalid_limits()
    {
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d => d.Cpus = 0).Validate());
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d => d.MemoryMb = 64).Validate());
        Assert.Throws<InvalidOperationException>(() => ValidSpec(d => d.Pids = 0).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            ValidSpec(d => d.Timeout = TimeSpan.Zero).Validate());
    }

    // ---- validated workspace (Blocker 1) ----------------------------------------

    [Fact]
    public void Spec_requires_a_validated_workspace_value_object()
    {
        var noPath = new SandboxSpec
        {
            Role = SandboxRole.Coder,
            Image = "sha256:" + new string('a', 64),
            Network = SandboxNetworkPolicy.Model,
            Cpus = 4.0,
            MemoryMb = 8192,
            Pids = 256,
            Timeout = TimeSpan.FromMinutes(30),
        };
        var ex = Assert.Throws<InvalidOperationException>(() => noPath.Validate());
        Assert.Contains("validated disposable host workspace", ex.Message);
        // The property type is the value object, never a raw string.
        Assert.Equal(typeof(ValidatedSandboxWorkspacePath),
            typeof(SandboxSpec).GetProperty("HostWorkspacePath")!.PropertyType);
    }

    [Fact]
    public void Validated_workspace_value_is_not_serializable_or_loggable()
    {
        var workspace = ValidatedSandboxWorkspacePath.Create(
            NewWorkspaceDir(), _managedRoot.Root, _repo.Root);

        var json = JsonSerializer.Serialize(workspace);
        Assert.DoesNotContain(workspace.Value, json);
        Assert.DoesNotContain("Value", json);

        Assert.DoesNotContain(workspace.Value, workspace.ToString());
    }

    // ---- host-path containment (agent/session-facing contract) -------------------

    [Fact]
    public void Agent_session_facing_contract_exposes_no_host_paths()
    {
        foreach (var type in new[] { typeof(ISandboxSession), typeof(SandboxSessionInfo) })
        {
            Assert.All(type.GetProperties(), p =>
            {
                Assert.NotEqual("hostworkspacepath", p.Name.ToLowerInvariant());
                Assert.NotEqual("repopath", p.Name.ToLowerInvariant());
                Assert.NotEqual("spec", p.Name.ToLowerInvariant());
            });
        }
    }

    [Fact]
    public void Session_info_serializes_without_any_host_path()
    {
        const string hostScratch = "/tmp/tenninety/private/attempt-9";
        const string authoritativeRepo = "/home/user/authoritative-checkout";
        var info = new SandboxSessionInfo(
            "container-123", SandboxRole.Coder, SandboxSessionState.Running);

        var json = JsonSerializer.Serialize(info);
        Assert.DoesNotContain("HostWorkspacePath", json);
        Assert.DoesNotContain("hostWorkspacePath", json);
        Assert.DoesNotContain("RepoPath", json);
        Assert.DoesNotContain(hostScratch, json);
        Assert.DoesNotContain(authoritativeRepo, json);
        Assert.Contains("/workspace", json); // the only workspace path it may carry
    }

    // ---- guest working directory -------------------------------------------------

    [Theory]
    [InlineData("/workspace")]
    [InlineData("/workspace/src")]
    [InlineData("/workspace/a directory")]
    public void Guest_working_directory_accepts_exact_workspace_paths(string workingDirectory)
    {
        Assert.True(SandboxCommand.IsSafeGuestWorkingDirectory(workingDirectory));
        new SandboxCommand
        {
            Executable = "dotnet",
            WorkingDirectory = workingDirectory,
        }.Validate(TimeSpan.FromMinutes(1));
    }

    [Theory]
    [InlineData("/workspace-escape")]
    [InlineData("/workspace/../etc")]
    [InlineData("/workspace/src/../../etc")]
    [InlineData("/workspace//../etc")]
    [InlineData("/etc")]
    [InlineData("workspace/src")]
    [InlineData("/workspace/src\0")]
    [InlineData("/workspace/./x")]
    [InlineData("/workspace//x")]
    [InlineData("/workspace/")]
    [InlineData("/workspace/src/")]
    [InlineData("/Workspace/src")]
    [InlineData("/workspace\\src")]
    [InlineData("/workspaceX/src")]
    [InlineData("")]
    public void Guest_working_directory_rejects_escape_and_malformed_paths(string workingDirectory)
    {
        Assert.False(SandboxCommand.IsSafeGuestWorkingDirectory(workingDirectory));
        Assert.Throws<InvalidOperationException>(() => new SandboxCommand
        {
            Executable = "dotnet",
            WorkingDirectory = workingDirectory,
        }.Validate(TimeSpan.FromMinutes(1)));
    }

    // ---- command ----------------------------------------------------------------

    [Fact]
    public void Command_defaults_to_workspace_and_bounded_output()
    {
        var command = new SandboxCommand { Executable = "dotnet" };
        Assert.Equal("/workspace", command.WorkingDirectory);
        Assert.True(command.MaxOutputBytes > 0);
        command.Validate(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Command_validation_enforces_bounds()
    {
        Assert.Throws<InvalidOperationException>(() => new SandboxCommand
        {
            Executable = "dotnet",
            Timeout = TimeSpan.FromHours(2),
        }.Validate(TimeSpan.FromMinutes(30)));
        Assert.Throws<InvalidOperationException>(() => new SandboxCommand
        {
            Executable = "dotnet",
            Arguments = ["--project", "a\0b"],
        }.Validate(TimeSpan.FromMinutes(30)));
        Assert.Throws<InvalidOperationException>(() => new SandboxCommand
        {
            Executable = "",
        }.Validate(TimeSpan.FromMinutes(30)));
        Assert.Throws<InvalidOperationException>(() => new SandboxCommand
        {
            Executable = "dotnet",
            MaxOutputBytes = 0,
        }.Validate(TimeSpan.FromMinutes(30)));
        new SandboxCommand
        {
            Executable = "dotnet",
            Arguments = ["test", "-c", "Release"],
            Environment = new Dictionary<string, string> { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
        }.Validate(TimeSpan.FromMinutes(30));
    }

    // ---- closed environment and label policies (Blocker 3) -----------------------

    [Fact]
    public void Spec_accepts_every_explicitly_permitted_environment_key()
    {
        BuildSpec(env: new Dictionary<string, string>
        {
            ["TENNINETY_WP"] = "WP-001",
            ["TENNINETY_ATTEMPT"] = "3",
        }).Validate();

        // Coder additionally may carry the scoped local-model token.
        BuildSpec(env: new Dictionary<string, string> { ["OPENAI_API_KEY"] = "scoped-local-token" })
            .Validate();

        BuildSpec(SandboxRole.Tester, env: new Dictionary<string, string>
        {
            ["TENNINETY_WP"] = "WP-001",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        }).Validate();

        // Restore may point at the restricted proxy boundary.
        BuildSpec(SandboxRole.Restore, env: new Dictionary<string, string>
        {
            ["TENNINETY_WP"] = "WP-001",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["HTTPS_PROXY"] = "http://restore-proxy:3128",
            ["NO_PROXY"] = "coder-model",
        }).Validate();
    }

    [Theory]
    [InlineData("HOME")]
    [InlineData("PATH")]
    [InlineData("DOCKER_HOST")]
    [InlineData("DOCKER_CONFIG")]
    [InlineData("SSH_AUTH_SOCK")]
    [InlineData("GIT_CONFIG_GLOBAL")]
    [InlineData("GIT_CONFIG_SYSTEM")]
    [InlineData("LD_PRELOAD")]
    [InlineData("BASH_ENV")]
    [InlineData("ENV")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("AZURE_CLIENT_SECRET")]
    [InlineData("TENNINETY_FRONTIER_API_KEY")]
    [InlineData("GITHUB_TOKEN")]
    public void Spec_rejects_forbidden_environment_keys(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BuildSpec(
            env: new Dictionary<string, string> { [key] = "value" }).Validate());
        Assert.Contains("closed per-role allowlist", ex.Message);
    }

    [Fact]
    public void Scoped_model_token_is_coder_only()
    {
        Assert.Throws<InvalidOperationException>(() => BuildSpec(
            SandboxRole.Reviewer,
            env: new Dictionary<string, string> { ["OPENAI_API_KEY"] = "x" }).Validate());
        Assert.Throws<InvalidOperationException>(() => BuildSpec(
            SandboxRole.Tester,
            env: new Dictionary<string, string> { ["OPENAI_API_KEY"] = "x" }).Validate());
    }

    [Fact]
    public void Command_environment_uses_its_own_closed_allowlist()
    {
        new SandboxCommand
        {
            Executable = "dotnet",
            Environment = new Dictionary<string, string>
            {
                ["TENNINETY_WP"] = "WP-001",
                ["TENNINETY_ATTEMPT"] = "2",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
            },
        }.Validate(TimeSpan.FromMinutes(5));

        foreach (var key in new[] { "HOME", "PATH", "DOCKER_HOST", "LD_PRELOAD", "SSH_AUTH_SOCK" })
        {
            Assert.Throws<InvalidOperationException>(() => new SandboxCommand
            {
                Executable = "dotnet",
                Environment = new Dictionary<string, string> { [key] = "value" },
            }.Validate(TimeSpan.FromMinutes(5)));
        }
    }

    [Fact]
    public void Labels_are_restricted_to_tenninety_management_identity_keys()
    {
        var all = new Dictionary<string, string>();
        foreach (var key in SandboxPolicy.PermittedLabelKeys)
            all[key] = key == "tenninety.role"
                ? RoleName(SandboxRole.Coder)
                : key == "tenninety.candidate" ? new string('a', 40) : "identity-value";
        BuildSpec(labels: all, candidateSha: new string('a', 40)).Validate();

        foreach (var bad in new[] { "role", "com.example.custom", "tenninety.extra", "TenNinety.WP" })
        {
            Assert.Throws<InvalidOperationException>(() => BuildSpec(
                labels: new Dictionary<string, string> { [bad] = "v" }).Validate());
        }
    }

    [Fact]
    public void Label_and_environment_values_reject_control_characters_and_overlong_values()
    {
        Assert.Throws<InvalidOperationException>(() => BuildSpec(
            labels: CompleteLabelsWith("tenninety.wp", "a\0b")).Validate());
        Assert.Throws<InvalidOperationException>(() => BuildSpec(
            labels: CompleteLabelsWith("tenninety.wp", "line1\nline2")).Validate());
        Assert.Throws<InvalidOperationException>(() => BuildSpec(
            labels: CompleteLabelsWith("tenninety.wp", new string('x', 257)))
            .Validate());
        Assert.Throws<InvalidOperationException>(() => BuildSpec(
            env: new Dictionary<string, string> { ["TENNINETY_WP"] = "a\tb" }).Validate());
        Assert.Throws<InvalidOperationException>(() => BuildSpec(
            env: new Dictionary<string, string> { ["TENNINETY_WP"] = new string('x', 1025) })
            .Validate());

        // Boundary values are fine.
        BuildSpec(labels: CompleteLabelsWith("tenninety.wp", new string('x', 256)))
            .Validate();
        BuildSpec(env: new Dictionary<string, string> { ["TENNINETY_WP"] = new string('x', 1024) })
            .Validate();
    }

    private static Dictionary<string, string> CompleteLabelsWith(string key, string value)
    {
        var labels = CompleteLabels(SandboxRole.Coder);
        labels[key] = value;
        return labels;
    }

    // ---- defensive snapshots (Blocker 3) -----------------------------------------

    [Fact]
    public void Spec_collections_are_frozen_snapshots()
    {
        var labels = CompleteLabels(SandboxRole.Coder);
        var environment = new Dictionary<string, string> { ["TENNINETY_WP"] = "WP-001" };
        var spec = BuildSpec(env: environment, labels: labels);
        spec.Validate();

        // Mutate the ORIGINAL collections after construction and validation, in ways that
        // would individually make the spec invalid.
        labels["tenninety.wp"] = "EVIL";
        labels.Remove("tenninety.run");
        labels["tenninety.role"] = "reviewer";
        environment["TENNINETY_WP"] = "EVIL";
        environment.Add("LD_PRELOAD", "/evil.so");

        // The spec is unaffected and still validates against the original frozen snapshot.
        spec.Validate();
        Assert.Equal("WP-001", spec.Labels["tenninety.wp"]);
        Assert.Equal("coder", spec.Labels["tenninety.role"]);
        Assert.True(spec.Labels.ContainsKey("tenninety.run"));
        Assert.Equal(SandboxSpec.RequiredLabelKeys.Count, spec.Labels.Count);
        Assert.Equal("WP-001", spec.Environment["TENNINETY_WP"]);
        Assert.Single(spec.Environment);

        // The returned surfaces cannot be cast back to mutable types.
        Assert.Throws<InvalidCastException>(() => (Dictionary<string, string>)spec.Labels);
        Assert.Throws<InvalidCastException>(() => (Dictionary<string, string>)spec.Environment);
    }

    [Fact]
    public void Command_collections_are_frozen_snapshots()
    {
        var arguments = new List<string> { "exec", "container-1" };
        var environment = new Dictionary<string, string> { ["TENNINETY_WP"] = "WP-001" };
        var command = new SandboxCommand
        {
            Executable = "dotnet",
            Arguments = arguments,
            Environment = environment,
        };
        command.Validate(TimeSpan.FromMinutes(5));

        // Mutate the ORIGINAL collections after construction and validation.
        arguments.Add("--evil");
        arguments[0] = "sh";
        environment["TENNINETY_WP"] = "EVIL";
        environment.Add("LD_PRELOAD", "/evil.so");

        // The command is unaffected and still validates against the original snapshot.
        command.Validate(TimeSpan.FromMinutes(5));
        Assert.Equal(new[] { "exec", "container-1" }, command.Arguments);
        Assert.Equal("WP-001", command.Environment["TENNINETY_WP"]);
        Assert.Single(command.Environment);

        Assert.Throws<InvalidCastException>(() => (List<string>)command.Arguments);
        Assert.Throws<InvalidCastException>(() => (Dictionary<string, string>)command.Environment);
    }

    [Fact]
    public void Fixed_tmpfs_policy_cannot_be_mutated_through_the_returned_collection()
    {
        var mounts = SandboxPolicy.FixedTmpfsMounts;
        Assert.Equal(2, mounts.Count);
        // The frozen collection cannot be cast to a mutable array.
        Assert.Throws<InvalidCastException>(() => (TmpfsMount[])mounts);
        Assert.Throws<InvalidCastException>(() => (List<TmpfsMount>)mounts);
        Assert.Contains(mounts, m => m.ContainerPath == "/tmp" && m.Options.Contains("size=512m"));
    }

    // ---- result (success semantics) ---------------------------------------------

    [Fact]
    public void Clean_exit_zero_result_succeeds()
    {
        var ok = new SandboxCommandResult(0, "", "", false, false, false, false, TimeSpan.FromSeconds(1));
        Assert.True(ok.Succeeded);
    }

    [Theory]
    [InlineData(true, false, false, false)] // timed out
    [InlineData(false, true, false, false)] // cancelled
    [InlineData(false, false, true, false)] // oom killed
    [InlineData(false, false, false, true)] // output truncated
    public void Any_termination_or_truncation_flag_prevents_success(
        bool timedOut, bool cancelled, bool oomKilled, bool outputTruncated)
    {
        var result = new SandboxCommandResult(
            ExitCode: 0, StdOutTail: "", StdErrTail: "",
            TimedOut: timedOut, Cancelled: cancelled, OomKilled: oomKilled,
            OutputTruncated: outputTruncated, Duration: TimeSpan.FromSeconds(1));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Nonzero_exit_prevents_success()
    {
        var failed = new SandboxCommandResult(1, "", "boom", false, false, false, false, TimeSpan.Zero);
        Assert.False(failed.Succeeded);
    }

    // ---- session ----------------------------------------------------------------

    [Fact]
    public async Task Session_is_quiescent_only_after_confirmed_stop()
    {
        await using var session = new FakeSandboxSession(SandboxRole.Coder);
        Assert.Equal(SandboxSessionState.Created, session.Info.State);
        Assert.False(session.WritesQuiescent);

        await session.RunAsync(new SandboxCommand { Executable = "true" });
        Assert.Equal(SandboxSessionState.Running, session.Info.State);
        Assert.False(session.WritesQuiescent);

        await session.StopAsync();
        Assert.Equal(SandboxSessionState.StoppedQuiescent, session.Info.State);
        Assert.True(session.WritesQuiescent);
    }

    [Fact]
    public async Task Session_info_carries_only_sanitized_identity()
    {
        var session = new FakeSandboxSession(SandboxRole.Coder);
        var info = session.Info;
        Assert.Equal(SandboxRole.Coder, info.Role);
        Assert.Equal("/workspace", info.ContainerWorkspacePath);
        Assert.StartsWith("fake-", info.ContainerId);
        Assert.False(session.WritesQuiescent);
        await session.StopAsync();
        Assert.True(session.WritesQuiescent);
    }

    [Fact]
    public async Task Session_refuses_commands_after_stop()
    {
        var session = new FakeSandboxSession(SandboxRole.Coder);
        await session.StopAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunAsync(new SandboxCommand { Executable = "true" }));
    }

    [Fact]
    public async Task Runtime_creates_sessions_from_validated_specs()
    {
        var runtime = new FakeSandboxRuntime();
        var session = await runtime.CreateAsync(ValidSpec());
        Assert.Equal(SandboxRole.Coder, session.Info.Role);
        Assert.Single(runtime.CreatedSpecs);
        Assert.False(session.WritesQuiescent);
    }

    private sealed class FakeSandboxRuntime : ISandboxRuntime
    {
        public readonly List<SandboxSpec> CreatedSpecs = new();

        public Task<ISandboxSession> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            spec.Validate();
            CreatedSpecs.Add(spec);
            return Task.FromResult<ISandboxSession>(new FakeSandboxSession(spec.Role));
        }
    }

    // ---- docker transport seam --------------------------------------------------

    [Fact]
    public async Task Transport_records_the_exact_argument_vector_and_returns_scripted_results()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(new DockerCliResult(0, "Docker version 29", "", TimedOut: false,
            Cancelled: false, OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)));

        var invocation = new DockerCliInvocation(["version", "--format", "{{.Server.Version}}"]);
        var result = await transport.RunAsync(invocation);

        Assert.True(result.Succeeded);
        Assert.Equal(1, transport.CallCount);
        Assert.Equal(new[] { "version", "--format", "{{.Server.Version}}" }, transport.Invocations[0].Arguments);
        transport.AssertNoHostShellInvocation();
    }

    [Fact]
    public async Task Arguments_with_spaces_and_metacharacters_stay_one_literal_argument()
    {
        var transport = new FakeDockerCliTransport();
        const string instruction = "implement WP-001; $(rm -rf /) & | `whoami` \"quoted\" tail";
        var args = new List<string>
        {
            "exec", "container-1", "aider", "--message", instruction, "a b c",
        };

        var invocation = new DockerCliInvocation(args);
        await transport.RunAsync(invocation);

        var recorded = transport.Invocations[0].Arguments;
        Assert.Equal(args.Count, recorded.Count);
        Assert.Equal(instruction, recorded[4]); // metacharacters never split or evaluated
        Assert.Equal("a b c", recorded[5]);     // spaces never split one argument into three
        transport.AssertNoHostShellInvocation();
    }

    [Fact]
    public async Task Shell_executables_are_never_recorded_as_the_transport_entry_point()
    {
        var transport = new FakeDockerCliTransport();
        // The Tester's in-container shell is an EXEC PAYLOAD argument to `docker exec`,
        // never the host process the transport would launch.
        var invocation = new DockerCliInvocation(["exec", "container-1", "/bin/bash",
            "--noprofile", "--norc", "-c", "dotnet test"]);
        await transport.RunAsync(invocation);
        transport.AssertNoHostShellInvocation();
    }

    [Fact]
    public void Transport_contract_has_no_single_string_shell_overload()
    {
        var runMethods = typeof(IDockerCliTransport).GetMethods()
            .Where(m => m.Name == nameof(IDockerCliTransport.RunAsync)).ToList();
        Assert.All(runMethods, m => Assert.Equal(
            typeof(DockerCliInvocation), m.GetParameters()[0].ParameterType));
    }

    [Fact]
    public async Task Fake_transport_timeout_result_is_not_success()
    {
        var transport = new FakeDockerCliTransport();
        transport.Enqueue(new DockerCliResult(-1, "", "timed out", TimedOut: true,
            Cancelled: false, OutputTruncated: false, Duration: TimeSpan.Zero));
        var invocation = new DockerCliInvocation(["stop", "c1"]);
        var result = await transport.RunAsync(invocation);
        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
    }

    // ---- candidate records ------------------------------------------------------

    [Fact]
    public void Candidate_records_bind_workspace_to_exact_revision()
    {
        var revision = new CandidateRevision("work/WP-001",
            "0123456789abcdef0123456789abcdef01234567",
            "fedcba9876543210fedcba9876543210fedcba98");
        var workspace = new CandidateWorkspace(
            revision,
            "/tmp/tenninety/run1/WP-001/attempt-3-coder",
            "/tmp/tenninety/run1/WP-001/attempt-3-coder/source",
            "/tmp/tenninety/run1/WP-001/attempt-3-coder/ingestion",
            "4b825dc642cb6eb9a060e54bf8d69288fbee4904",
            SandboxRole.Coder, "run1", "attempt-3-coder");

        Assert.Equal(revision, workspace.Revision);
        Assert.Equal(SandboxRole.Coder, workspace.Role);
        Assert.StartsWith(workspace.AttemptRoot, workspace.SourcePath, StringComparison.Ordinal);

        var change = new CandidateChange(
            "src/Foo.cs", GitChangeKind.Added, "", "100644",
            OldObjectHash: null, NewObjectHash: "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391",
            ByteSize: 12);
        var patch = new CandidatePatch(
            revision.CommitSha, "4b825dc642cb6eb9a060e54bf8d69288fbee4904",
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "/tmp/tenninety/run1/WP-001/attempt-3-coder/promotion.patch",
            "deadbeef", [change]);
        Assert.Single(patch.Changes);
        Assert.Equal(GitChangeKind.Added, patch.Changes[0].Kind);
    }
}

/// <summary>
/// Tests for the <see cref="ValidatedSandboxWorkspacePath"/> trusted value object: it is only
/// produced for a real, canonical, disposable directory strictly beneath the managed root that
/// does not overlap the authoritative repository and contains no redirecting symlinks.
/// All fixtures use temporary directories that are cleaned safely.
/// </summary>
public class SandboxAbstractionWorkspacePathTests : IDisposable
{
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _outside = new();
    private readonly TempDir _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
        _outside.Dispose();
        _managedRoot.Dispose();
    }

    private string Child(string relative) =>
        Directory.CreateDirectory(Path.Combine(_managedRoot.Root, relative)).FullName;

    [Fact]
    public void Accepts_a_genuine_temporary_child_directory()
    {
        var workspace = ValidatedSandboxWorkspacePath.Create(
            Child("attempt-1"), _managedRoot.Root, _repo.Root);

        Assert.Equal(Child("attempt-1"), workspace.Value);
        Assert.True(Directory.Exists(workspace.Value));
    }

    [Fact]
    public void Rejects_the_filesystem_root()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create("/", _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_a_directory_outside_the_managed_root()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_outside.Root, "attempt"));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(outside.FullName, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_the_managed_workspace_root_itself()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(_managedRoot.Root, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_the_authoritative_repository()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(_repo.Root, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_a_child_of_the_authoritative_repository()
    {
        var repoChild = Directory.CreateDirectory(Path.Combine(_repo.Root, "child"));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(repoChild.FullName, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_a_child_of_the_repository_even_inside_the_managed_root()
    {
        var repo = Child("repo");
        var child = Directory.CreateDirectory(Path.Combine(repo, "child")).FullName;
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(child, _managedRoot.Root, repo));
    }

    [Fact]
    public void Rejects_a_parent_of_the_authoritative_repository()
    {
        var repo = Child("level1/repo");
        var parent = Child("level1"); // strictly beneath the root, ancestor of the repo
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(parent, _managedRoot.Root, repo));
    }

    [Fact]
    public void Rejects_prefix_collision_paths()
    {
        var escape = _managedRoot.Root + "-escape";
        var child = Directory.CreateDirectory(Path.Combine(escape, "attempt")).FullName;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ValidatedSandboxWorkspacePath.Create(child, _managedRoot.Root, _repo.Root));
        }
        finally
        {
            Directory.Delete(escape, recursive: true);
        }
    }

    [Fact]
    public void Rejects_relative_and_traversal_paths()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create("relative/path", _managedRoot.Root, _repo.Root));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create("../escape", _managedRoot.Root, _repo.Root));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(_managedRoot.Root + "/../escape", _managedRoot.Root, _repo.Root));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(_managedRoot.Root + "/./attempt", _managedRoot.Root, _repo.Root));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(_managedRoot.Root + "/a//b", _managedRoot.Root, _repo.Root));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(_managedRoot.Root + "/a\\b", _managedRoot.Root, _repo.Root));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create("", _managedRoot.Root, _repo.Root));
    }

    // ---- mandatory authoritative repository (Blocker 1) ---------------------------

    [Fact]
    public void Factory_requires_a_repository_argument()
    {
        var create = typeof(ValidatedSandboxWorkspacePath).GetMethods()
            .Where(m => m.Name == nameof(ValidatedSandboxWorkspacePath.Create) && m.IsPublic)
            .ToList();
        Assert.Single(create);
        var parameters = create[0].GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal("authoritativeRepositoryPath", parameters[2].Name);
        // No optional/default parameter exists: the factory cannot be called without a repository.
        Assert.All(parameters, p => Assert.False(p.HasDefaultValue));
    }

    [Fact]
    public void Rejects_relative_managed_roots()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "attempt"));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(workspace.FullName, "relative-root", _repo.Root));
    }

    [Fact]
    public void Rejects_relative_repository_paths()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "attempt"));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(workspace.FullName, _managedRoot.Root, "relative-repo"));
    }

    [Fact]
    public void Rejects_missing_repository_directories()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_managedRoot.Root, "attempt"));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(
                workspace.FullName, _managedRoot.Root, Path.Combine(_outside.Root, "missing-repo")));
    }

    [Fact]
    public void Rejects_a_symlink_ancestor_above_the_managed_root()
    {
        var realSub = Child("sub");
        var attempt = Directory.CreateDirectory(Path.Combine(realSub, "attempt")).FullName;
        var alias = Path.Combine(_outside.Root, "root-alias");
        Directory.CreateSymbolicLink(alias, _managedRoot.Root);
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(attempt, Path.Combine(alias, "sub"), _repo.Root));
    }

    [Fact]
    public void Alias_rooted_under_the_repository_cannot_hide_the_repository()
    {
        // The essential regression: physical locations, not lexical strings, decide.
        // - physical repository directory: <repo>
        // - physical managed/attempt created INSIDE it
        // - symlink alias (outside) -> <repo>
        // - managedRoot passed as alias/managed, candidate as alias/managed/attempt,
        //   repository passed as the PHYSICAL repository path.
        // Creation must throw: the physical candidate lives inside the physical repository.
        var repo = _repo.Root;
        var managed = Directory.CreateDirectory(Path.Combine(repo, "managed")).FullName;
        var attempt = Directory.CreateDirectory(Path.Combine(managed, "attempt")).FullName;
        var alias = Path.Combine(_outside.Root, "alias");
        Directory.CreateSymbolicLink(alias, repo);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                ValidatedSandboxWorkspacePath.Create(
                    attempt, Path.Combine(alias, "managed"), repo));
        }
        finally
        {
            // Only Tenninety-created fixture content is removed; the repo temp dir is
            // cleaned by its own TempDir disposable.
            Directory.Delete(managed, recursive: true);
        }
    }

    [Fact]
    public void Repository_symlink_alias_cannot_bypass_overlap_detection()
    {
        // The repository argument reaches the real repo through a symlink alias; the
        // candidate physically lives inside the real repository. Lexically the alias
        // contains nothing, so only physical resolution can detect the overlap.
        var repo = _repo.Root;
        var child = Directory.CreateDirectory(Path.Combine(repo, "child")).FullName;
        var alias = Path.Combine(_outside.Root, "repo-alias");
        Directory.CreateSymbolicLink(alias, repo);
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(child, _managedRoot.Root, alias));
    }

    [Fact]
    public void Alias_to_the_managed_root_itself_cannot_bypass_overlap_detection()
    {
        // The repository alias points AT the managed root: every workspace beneath the root
        // physically sits inside the repository, so overlap detection must fire.
        var attempt = Child("attempt");
        var alias = Path.Combine(_outside.Root, "root-as-repo-alias");
        Directory.CreateSymbolicLink(alias, _managedRoot.Root);
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(attempt, _managedRoot.Root, alias));
    }

    [Fact]
    public void Error_messages_never_contain_input_paths()
    {
        var repo = _repo.Root;
        var attempt = Child("attempt");

        // Containment failure (candidate outside the managed root).
        var outside = Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(repo, _managedRoot.Root, repo));
        Assert.DoesNotContain(attempt, outside.Message);
        Assert.DoesNotContain(_managedRoot.Root, outside.Message);
        Assert.DoesNotContain(repo, outside.Message);

        // Genuine overlap failure: the repository alias points at the managed root, so the
        // candidate physically lives inside the repository.
        var alias = Path.Combine(_outside.Root, "msg-repo-alias");
        Directory.CreateSymbolicLink(alias, _managedRoot.Root);
        var overlap = Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(attempt, _managedRoot.Root, alias));
        Assert.DoesNotContain(attempt, overlap.Message);
        Assert.DoesNotContain(_managedRoot.Root, overlap.Message);
        Assert.DoesNotContain(alias, overlap.Message);

        // Missing repository.
        var missingRepo = Path.Combine(_outside.Root, "no-such-repo");
        var missing = Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(attempt, _managedRoot.Root, missingRepo));
        Assert.DoesNotContain(attempt, missing.Message);
        Assert.DoesNotContain(_managedRoot.Root, missing.Message);
        Assert.DoesNotContain(missingRepo, missing.Message);

        // Symlink ancestor above the managed root.
        var rootAlias = Path.Combine(_outside.Root, "msg-alias");
        Directory.CreateSymbolicLink(rootAlias, _managedRoot.Root);
        var ancestor = Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(
                Child("sub/attempt"), Path.Combine(rootAlias, "sub"), repo));
        Assert.DoesNotContain(_managedRoot.Root, ancestor.Message);
        Assert.DoesNotContain(rootAlias, ancestor.Message);
        Assert.DoesNotContain(repo, ancestor.Message);
    }

    [Fact]
    public void Rejects_a_symlink_pointing_outside_the_managed_root()
    {
        var link = Path.Combine(_managedRoot.Root, "outside-link");
        Directory.CreateSymbolicLink(link, _outside.Root);
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(link, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_a_symlink_intermediate_component()
    {
        var target = Directory.CreateDirectory(Path.Combine(_outside.Root, "target"));
        var link = Path.Combine(_managedRoot.Root, "jump");
        Directory.CreateSymbolicLink(link, target.FullName);
        var through = Directory.CreateDirectory(Path.Combine(link, "child")).FullName;
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(through, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_a_symlink_pointing_at_the_authoritative_repository()
    {
        var link = Path.Combine(_managedRoot.Root, "repo-link");
        Directory.CreateSymbolicLink(link, _repo.Root);
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(link, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_missing_directories_and_plain_files()
    {
        var missing = Path.Combine(_managedRoot.Root, "never-created-attempt");
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(missing, _managedRoot.Root, _repo.Root));
        var file = Path.Combine(_managedRoot.Root, "plain-file");
        File.WriteAllText(file, "x");
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(file, _managedRoot.Root, _repo.Root));
    }

    [Fact]
    public void Rejects_the_repository_even_when_it_sits_inside_the_managed_root()
    {
        var repo = Child("repo");
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create(repo, _managedRoot.Root, repo));
    }

    [Fact]
    public void Rejects_unsafe_managed_roots()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create("/", "/", _repo.Root));
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && home != "/" && Directory.Exists(home))
            Assert.Throws<InvalidOperationException>(() =>
                ValidatedSandboxWorkspacePath.Create(
                    Path.Combine(home, "never-created"), home, _repo.Root));
        Assert.Throws<InvalidOperationException>(() =>
            ValidatedSandboxWorkspacePath.Create("/anything", "/nonexistent-tenninety-root", _repo.Root));
    }
}

/// <summary>
/// Tests for the mandatory, complete Tenninety management identity labels on every sandbox
/// spec (Blocker 2): all six required labels must be present with non-blank, safe values, the
/// role label must match the spec's normalized role, the repository label must be a non-secret
/// identity rather than a host path, and the candidate label must exactly track CandidateSha.
/// </summary>
public class SandboxAbstractionIdentityLabelTests : IDisposable
{
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _repo = new();

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private SandboxSpec Spec(
        SandboxRole role = SandboxRole.Coder,
        Dictionary<string, string>? labels = null,
        string? candidateSha = null)
    {
        var workspace = ValidatedSandboxWorkspacePath.Create(
            Directory.CreateDirectory(
                Path.Combine(_managedRoot.Root, "attempt-" + Guid.NewGuid().ToString("N"))).FullName,
            _managedRoot.Root, _repo.Root);
        return new SandboxSpec
        {
            Role = role,
            Image = "sha256:" + new string('a', 64),
            HostWorkspacePath = workspace,
            Network = role switch
            {
                SandboxRole.Coder => SandboxNetworkPolicy.Model,
                SandboxRole.Reviewer or SandboxRole.Tester => SandboxNetworkPolicy.None,
                SandboxRole.Restore => SandboxNetworkPolicy.Restore,
                _ => throw new ArgumentOutOfRangeException(nameof(role)),
            },
            Cpus = 4.0,
            MemoryMb = 8192,
            Pids = 256,
            Timeout = TimeSpan.FromMinutes(30),
            Labels = labels ?? SandboxAbstractionTests.CompleteLabels(role, candidateSha),
            Environment = new Dictionary<string, string> { ["TENNINETY_WP"] = "WP-001" },
            CandidateSha = candidateSha,
        };
    }

    [Fact]
    public void A_fully_labelled_specification_validates_for_every_role()
    {
        foreach (var role in Enum.GetValues<SandboxRole>())
            Spec(role).Validate();

        var sha = new string('a', 40);
        Spec(candidateSha: sha).Validate();
    }

    [Fact]
    public void Omitting_each_required_label_individually_fails()
    {
        foreach (var key in SandboxSpec.RequiredLabelKeys)
        {
            var labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
            labels.Remove(key);
            var ex = Assert.Throws<InvalidOperationException>(() => Spec(labels: labels).Validate());
            Assert.Contains($"'{key}'", ex.Message);
        }
    }

    [Fact]
    public void Empty_or_whitespace_only_required_values_fail()
    {
        foreach (var key in SandboxSpec.RequiredLabelKeys)
        foreach (var blank in new[] { "", "   " })
        {
            var labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
            labels[key] = blank;
            Assert.Throws<InvalidOperationException>(() => Spec(labels: labels).Validate());
        }
    }

    [Fact]
    public void A_mismatched_role_label_fails_for_every_role()
    {
        foreach (var role in Enum.GetValues<SandboxRole>())
        {
            Spec(role).Validate(); // the internally consistent label passes

            foreach (var wrong in Enum.GetValues<SandboxRole>().Where(r => r != role))
            {
                var labels = SandboxAbstractionTests.CompleteLabels(role);
                labels["tenninety.role"] = SandboxAbstractionTests.RoleName(wrong);
                var ex = Assert.Throws<InvalidOperationException>(
                    () => Spec(role, labels: labels).Validate());
                Assert.Contains("tenninety.role", ex.Message);
            }
        }
    }

    [Fact]
    public void An_absolute_host_path_repository_identity_fails()
    {
        foreach (var bad in new[] { "/home/user/repo", "/", "/repo", "C:\\repo", "C:/repo" })
        {
            var labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
            labels["tenninety.repository"] = bad;
            var ex = Assert.Throws<InvalidOperationException>(
                () => Spec(labels: labels).Validate());
            Assert.Contains("non-secret repository identity", ex.Message);
        }
    }

    [Fact]
    public void Candidate_label_and_candidate_sha_mismatches_fail()
    {
        var sha = new string('a', 40);

        // Present and matching: valid.
        Spec(candidateSha: sha).Validate();

        // CandidateSha set but the label is missing.
        var missingLabel = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
        missingLabel.Remove("tenninety.candidate");
        Assert.Throws<InvalidOperationException>(
            () => Spec(candidateSha: sha, labels: missingLabel).Validate());

        // CandidateSha set but the label differs.
        var wrongLabel = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder, sha);
        wrongLabel["tenninety.candidate"] = new string('b', 40);
        Assert.Throws<InvalidOperationException>(
            () => Spec(candidateSha: sha, labels: wrongLabel).Validate());

        // Label present without a CandidateSha.
        var labelWithoutSha = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
        labelWithoutSha["tenninety.candidate"] = sha;
        Assert.Throws<InvalidOperationException>(
            () => Spec(labels: labelWithoutSha).Validate());
    }

    [Fact]
    public void Unknown_labels_still_fail()
    {
        var labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
        labels["tenninety.extra"] = "unrecognized";
        Assert.Throws<InvalidOperationException>(() => Spec(labels: labels).Validate());
    }

    [Fact]
    public void Mutation_after_construction_remains_impossible()
    {
        var labels = SandboxAbstractionTests.CompleteLabels(SandboxRole.Coder);
        var spec = Spec(labels: labels);
        spec.Validate();

        // Mutate the ORIGINAL dictionary in ways that would individually invalidate the spec.
        labels.Remove("tenninety.run");
        labels["tenninety.role"] = SandboxAbstractionTests.RoleName(SandboxRole.Reviewer);
        labels.Add("tenninety.extra", "unrecognized");

        // The defensive snapshot keeps the original complete identity: still valid.
        spec.Validate();
        Assert.Equal(SandboxSpec.RequiredLabelKeys.Count, spec.Labels.Count);
        Assert.Equal("coder", spec.Labels["tenninety.role"]);
        Assert.True(spec.Labels.ContainsKey("tenninety.run"));
        Assert.False(spec.Labels.ContainsKey("tenninety.extra"));
    }
}
