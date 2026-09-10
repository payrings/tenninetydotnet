using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

public sealed class RestoreFlowTests : IDisposable
{
    private readonly TempDir _repo = new();
    private readonly TempDir _managedRoot = new();
    private readonly GitService _git;
    private readonly TenNinetyConfig _config;
    private readonly string _candidateSha;

    public RestoreFlowTests()
    {
        _git = new GitService(_repo.Root);
        _git.Init();
        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"),
            ".tenninety/*\n!.tenninety/.gitignore\n!.tenninety/config.json\n");
        Directory.CreateDirectory(Path.Combine(_repo.Root, ".tenninety"));
        File.WriteAllText(Path.Combine(_repo.Root, ".tenninety", ".gitignore"),
            Tenninety.Execution.RuntimeGitignoreMigration.Contents);
        File.WriteAllText(Path.Combine(_repo.Root, ".tenninety", "config.json"), "{}\n");
        File.WriteAllText(Path.Combine(_repo.Root, "tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" />" +
            "</ItemGroup></Project>");
        // Restore runs 'dotnet restore --locked-mode': every restored project must carry a
        // committed, consistent packages.lock.json, so the fixture candidate includes one.
        File.WriteAllText(Path.Combine(_repo.Root, "packages.lock.json"),
            RestoreFlowLockFile);
        File.WriteAllText(Path.Combine(_repo.Root, "source.txt"), "candidate\n");
        _candidateSha = _git.CommitAll("candidate")!;

        _config = new TenNinetyConfig
        {
            ProviderMode = "aider",
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
                    Reviewer = new ReviewerSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.ReviewerImageId,
                    },
                    Tester = new TesterSandboxRoleConfig
                    {
                        Image = PreflightFakeTransport.TesterImageId,
                    },
                },
            },
        };
        ConfigureRestore(_config.Sandbox.Roles.Tester.Restore);
    }

    [Fact]
    public async Task Accepted_restore_uses_fixed_feeds_then_a_fresh_offline_tester()
    {
        var timeline = new List<string>();
        var runtime = new RoleRuntime(timeline);
        string? controlXml = null;
        UnixFileMode? candidateParentMode = null;
        runtime.Factory = spec =>
        {
            var session = Session(spec, timeline);
            if (spec.Role == SandboxRole.Restore)
            {
                session.OnRun = command =>
                {
                    controlXml = File.ReadAllText(Path.Combine(
                        spec.HostWorkspacePath!.Value,
                        ".tenninety", "restore-control", "NuGet.Config"));
                    if (OperatingSystem.IsLinux())
                        candidateParentMode = File.GetUnixFileMode(Path.Combine(
                            spec.HostWorkspacePath.Value, ".tenninety"));
                    WriteDerived(spec, ".tenninety/restore-packages/pkg/data.bin", "package");
                    WriteDerived(spec, "obj/project.assets.json", "assets");
                };
            }
            return session;
        };
        var gate = Gate(runtime, timeline, out var transport);

        var result = await gate.RunTestsAsync(Context());

        Assert.True(result.Passed, result.OutputTail);
        Assert.Matches("^[0-9a-f]{64}$", result.RestoreOutputSha256!);
        Assert.Equal([SandboxRole.Restore, SandboxRole.Tester],
            runtime.Specs.Select(spec => spec.Role));
        Assert.Equal(SandboxNetworkPolicy.Restore, runtime.Specs[0].Network);
        Assert.Equal(SandboxNetworkPolicy.None, runtime.Specs[1].Network);
        Assert.True(timeline.IndexOf("restore:dispose") < timeline.IndexOf("create:tester"));
        var restoreCommand = runtime.Sessions[0].Commands.Single();
        Assert.Equal("/usr/bin/dotnet", restoreCommand.Executable);
        // The restore target is EXPLICIT (discovered by the bounded prerequisite scan), never
        // a working-directory inference.
        Assert.Equal(
            ["restore", "--locked-mode", "--configfile",
             "/workspace/.tenninety/restore-control/NuGet.Config", "--packages",
             "/workspace/.tenninety/restore-packages", "--nologo",
             "/workspace/tests.csproj"],
            restoreCommand.Arguments);
        Assert.Contains("<clear", controlXml);
        Assert.Contains("https://packages.example.test/v3/index.json", controlXml);
        Assert.Contains("https://mirror.example.test/v3/index.json", controlXml);
        Assert.DoesNotContain("api.nuget.org", controlXml);
        if (OperatingSystem.IsLinux())
            Assert.Equal((UnixFileMode)493, candidateParentMode); // committed parent materializes as 0755
        Assert.True(transport.Disposed);
        Assert.Equal("candidate\n", File.ReadAllText(Path.Combine(_repo.Root, "source.txt")));
        Assert.Equal("{}\n", File.ReadAllText(
            Path.Combine(_repo.Root, ".tenninety", "config.json")));
        Assert.Equal(_candidateSha, _git.HeadSha());
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Restore_without_a_candidate_tenninety_parent_still_reaches_tester()
    {
        File.Delete(Path.Combine(_repo.Root, ".tenninety", "config.json"));
        File.Delete(Path.Combine(_repo.Root, ".tenninety", ".gitignore"));
        var candidate = _git.CommitAll("remove candidate metadata")!;
        var timeline = new List<string>();
        var runtime = SuccessfulRuntime(timeline);
        var gate = Gate(runtime, timeline, out _);

        var result = await gate.RunTestsAsync(Context(candidate));

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal([SandboxRole.Restore, SandboxRole.Tester],
            runtime.Specs.Select(spec => spec.Role));
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Theory]
    [InlineData(448)] // 0700
    [InlineData(493)] // 0755
    public void Existing_candidate_parent_mode_is_preserved_while_control_is_owner_only(
        int parentMode)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var root = new TempDir();
        var parent = Directory.CreateDirectory(Path.Combine(root.Root, ".tenninety")).FullName;
        File.WriteAllText(Path.Combine(parent, "config.json"), "{}\n");
        File.SetUnixFileMode(parent, (UnixFileMode)parentMode);
        var validator = new RestoreIntegrityValidator();
        var baseline = validator.CaptureBaseline(
            root.Root, 1024 * 1024, 100, 16, default);

        SandboxTesterGate.CreateRestoreControl(
            root.Root, _config.Sandbox.Roles.Tester.Restore, baseline);

        Assert.Equal((UnixFileMode)parentMode, File.GetUnixFileMode(parent));
        Assert.Equal((UnixFileMode)448, File.GetUnixFileMode(
            Path.Combine(parent, "restore-control")));
        Assert.Equal((UnixFileMode)384, File.GetUnixFileMode(
            Path.Combine(parent, "restore-control", "NuGet.Config")));
    }

    [Fact]
    public void Newly_created_control_parent_is_owner_only()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var root = new TempDir();
        var validator = new RestoreIntegrityValidator();
        var baseline = validator.CaptureBaseline(
            root.Root, 1024 * 1024, 100, 16, default);

        SandboxTesterGate.CreateRestoreControl(
            root.Root, _config.Sandbox.Roles.Tester.Restore, baseline);

        Assert.Equal((UnixFileMode)448, File.GetUnixFileMode(
            Path.Combine(root.Root, ".tenninety")));
    }

    [Fact]
    public async Task Restore_output_outside_fixed_roots_blocks_tester_and_is_discarded()
    {
        var timeline = new List<string>();
        var runtime = new RoleRuntime(timeline)
        {
            Factory = spec =>
            {
                var session = Session(spec, timeline);
                if (spec.Role == SandboxRole.Restore)
                    session.OnRun = _ => WriteDerived(spec, "generated.cs", "outside");
                return session;
            },
        };
        var gate = Gate(runtime, timeline, out _);

        await Assert.ThrowsAsync<TesterInfrastructureException>(
            () => gate.RunTestsAsync(Context()));

        Assert.Equal([SandboxRole.Restore], runtime.Specs.Select(spec => spec.Role));
        Assert.Equal(_candidateSha, _git.HeadSha());
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Definitive_restore_failure_is_an_ordinary_gate_failure_without_tester_start()
    {
        var timeline = new List<string>();
        var runtime = new RoleRuntime(timeline)
        {
            Factory = spec =>
            {
                var session = Session(spec, timeline);
                if (spec.Role == SandboxRole.Restore)
                    session.Then(RecordingSandboxSession.Fail(7, stderr: "feed refused"));
                return session;
            },
        };
        var gate = Gate(runtime, timeline, out _);

        var result = await gate.RunTestsAsync(Context());

        Assert.False(result.Passed);
        Assert.Contains("Restore exited 7", result.OutputTail);
        Assert.Equal([SandboxRole.Restore], runtime.Specs.Select(spec => spec.Role));
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Multiple_projects_without_a_solution_restore_once_each_in_ordinal_order()
    {
        WriteProject("src/App.csproj");
        var candidate = _git.CommitAll("add second project")!;
        var timeline = new List<string>();
        var runtime = new RoleRuntime(timeline);
        var gate = Gate(runtime, timeline, out _);

        var result = await gate.RunTestsAsync(Context(candidate));

        Assert.True(result.Passed, result.OutputTail);
        var commands = runtime.Sessions[0].Commands;
        Assert.Equal(2, commands.Count);
        Assert.Equal(
            ["/workspace/src/App.csproj", "/workspace/tests.csproj"],
            commands.Select(RestoreTarget));
        Assert.All(commands, AssertOneRestoreTarget);
    }

    [Fact]
    public async Task Multiple_solutions_without_projects_restore_once_each()
    {
        File.Delete(Path.Combine(_repo.Root, "tests.csproj"));
        File.Delete(Path.Combine(_repo.Root, "packages.lock.json"));
        File.WriteAllText(Path.Combine(_repo.Root, "B.slnx"), "<Solution />\n");
        File.WriteAllText(Path.Combine(_repo.Root, "A.sln"), "\n");
        var candidate = _git.CommitAll("solutions only")!;
        var timeline = new List<string>();
        var runtime = new RoleRuntime(timeline);
        var gate = Gate(runtime, timeline, out _);

        var result = await gate.RunTestsAsync(Context(candidate));

        Assert.False(result.Passed);
        Assert.Contains("no test project", result.OutputTail);
        var commands = runtime.Sessions[0].Commands;
        Assert.Equal(["/workspace/A.sln", "/workspace/B.slnx"],
            commands.Select(RestoreTarget));
        Assert.All(commands, AssertOneRestoreTarget);
    }

    [Fact]
    public async Task One_solution_is_the_only_restore_target()
    {
        WriteProject("src/App.csproj");
        File.WriteAllText(Path.Combine(_repo.Root, "tenninety.slnx"), "<Solution />\n");
        var candidate = _git.CommitAll("add canonical solution")!;
        var timeline = new List<string>();
        var runtime = SuccessfulRuntime(timeline);
        var gate = Gate(runtime, timeline, out _);

        var result = await gate.RunTestsAsync(Context(candidate));

        Assert.True(result.Passed, result.OutputTail);
        var command = Assert.Single(runtime.Sessions[0].Commands);
        Assert.Equal("/workspace/tenninety.slnx", RestoreTarget(command));
        AssertOneRestoreTarget(command);
    }

    [Fact]
    public async Task Failure_on_a_later_target_stops_before_tester_and_names_the_target()
    {
        WriteProject("src/App.csproj");
        var candidate = _git.CommitAll("add second project")!;
        var timeline = new List<string>();
        var runtime = new RoleRuntime(timeline)
        {
            Factory = spec =>
            {
                var session = Session(spec, timeline);
                if (spec.Role == SandboxRole.Restore)
                    session.Then(RecordingSandboxSession.Ok())
                        .Then(RecordingSandboxSession.Fail(7));
                return session;
            },
        };
        var gate = Gate(runtime, timeline, out _);

        var result = await gate.RunTestsAsync(Context(candidate));

        Assert.False(result.Passed);
        Assert.Contains("target 2/2", result.OutputTail);
        Assert.Equal(2, runtime.Sessions[0].Commands.Count);
        Assert.All(runtime.Sessions[0].Commands, AssertOneRestoreTarget);
        Assert.Equal([SandboxRole.Restore], runtime.Specs.Select(spec => spec.Role));
        Assert.Contains("restore:dispose", timeline);
    }

    [Fact]
    public async Task Restore_removal_failure_retains_the_failing_target_diagnostic()
    {
        WriteProject("src/App.csproj");
        var candidate = _git.CommitAll("add second project")!;
        var timeline = new List<string>();
        var runtime = new RoleRuntime(timeline)
        {
            Factory = spec =>
            {
                var session = Session(spec, timeline);
                if (spec.Role == SandboxRole.Restore)
                {
                    session.Then(RecordingSandboxSession.Ok())
                        .Then(RecordingSandboxSession.Fail(7));
                    session.ThrowOnDispose = true;
                }
                return session;
            },
        };
        var gate = Gate(runtime, timeline, out _);

        var error = await Assert.ThrowsAsync<TesterInfrastructureException>(() =>
            gate.RunTestsAsync(Context(candidate)));

        Assert.Contains("target 2/2", error.Message);
        Assert.Contains("exited 7", error.Message);
        Assert.Contains("could not be proven removed", error.Message);
        Assert.Equal([SandboxRole.Restore], runtime.Specs.Select(spec => spec.Role));
    }

    [Fact]
    public async Task Caller_cancellation_on_a_later_target_stops_and_cleans_up()
    {
        WriteProject("src/App.csproj");
        var candidate = _git.CommitAll("add second project")!;
        var timeline = new List<string>();
        using var cancellation = new CancellationTokenSource();
        var restoreCalls = 0;
        var runtime = SuccessfulRuntime(timeline, command =>
        {
            if (++restoreCalls == 2) cancellation.Cancel();
        });
        var gate = Gate(runtime, timeline, out _);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.RunTestsAsync(Context(candidate), cancellation.Token));

        Assert.Equal(2, runtime.Sessions[0].Commands.Count);
        Assert.All(runtime.Sessions[0].Commands, AssertOneRestoreTarget);
        Assert.Contains("restore:dispose", timeline);
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
    }

    [Fact]
    public async Task Restore_targets_share_one_timeout_budget()
    {
        WriteProject("src/App.csproj");
        var candidate = _git.CommitAll("add second project")!;
        _config.Sandbox.Roles.Tester.Restore.TimeoutSeconds = 10;
        var timeline = new List<string>();
        var runtime = SuccessfulRuntime(timeline);
        var gate = Gate(runtime, timeline, out _);
        var elapsed = new Queue<TimeSpan>(
            [TimeSpan.Zero, TimeSpan.FromSeconds(6),
             TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(11)]);
        gate.RestoreElapsedOverride = () => elapsed.Dequeue();

        var error = await Assert.ThrowsAsync<TesterInfrastructureException>(() =>
            gate.RunTestsAsync(Context(candidate)));

        Assert.Contains("shared timeout budget exhausted", error.Message);
        var commands = runtime.Sessions[0].Commands;
        Assert.Equal(2, commands.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), commands[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(4), commands[1].Timeout);
        Assert.All(commands, AssertOneRestoreTarget);
        Assert.Contains("restore:dispose", timeline);
        Assert.Equal([SandboxRole.Restore], runtime.Specs.Select(spec => spec.Role));
    }

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private SandboxTesterGate Gate(
        RoleRuntime runtime,
        List<string> timeline,
        out SandboxTesterGateTests.ForwardingTransport transport)
    {
        var fake = new PreflightFakeTransport(_config.Sandbox);
        transport = new SandboxTesterGateTests.ForwardingTransport(fake, timeline.Add);
        var ownedTransport = transport;
        return new SandboxTesterGate(
            _git, _config, log: null,
            transportFactory: () => ownedTransport,
            runtimeFactory: (_, _) => runtime,
            preflightFactory: (cli, root) => new DockerSandboxPreflight(
                cli, _config.Sandbox, root, _repo.Root),
            deleteWorkspaceOverride: path =>
            {
                timeline.Add("delete");
                SandboxTesterGate.DeleteAttemptDirectory(path, _managedRoot.Root);
                return Task.CompletedTask;
            });
    }

    private const string RestoreFlowLockFile = """
        {
          "version": 1,
          "dependencies": {
            "net10.0": {
              "xunit": {
                "type": "Direct",
                "requested": "[2.9.3, )",
                "resolved": "2.9.3",
                "contentHash": "aaabbbcccdddeeeffffgggghhhhiiiijjjjkkkkllllmmmmnnnnooooppppqqqq"
              }
            }
          }
        }
        """;

    private static RecordingSandboxSession Session(SandboxSpec spec, List<string> timeline) => new()
    {
        Role = spec.Role,
        SourcePath = spec.HostWorkspacePath!.Value,
        EventSink = value => timeline.Add(spec.Role.ToString().ToLowerInvariant() + ":" + value),
    };

    private static void WriteDerived(SandboxSpec spec, string relative, string content)
    {
        var path = Path.Combine(
            spec.HostWorkspacePath!.Value,
            relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private TesterRunContext Context(string? candidateSha = null) => new()
    {
        Candidate = new CandidateRevision(
            "main", candidateSha ?? _candidateSha, candidateSha ?? _candidateSha),
        WorkPackageId = "WP-001",
        Attempt = 1,
    };

    private void WriteProject(string relative)
    {
        var project = Path.Combine(_repo.Root,
            relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>" +
            "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json"),
            "{\n  \"version\": 1,\n  \"dependencies\": {\n    \"net10.0\": {}\n  }\n}\n");
    }

    private RoleRuntime SuccessfulRuntime(
        List<string> timeline, Action<SandboxCommand>? onRestore = null) => new(timeline)
        {
            Factory = spec =>
            {
                var session = Session(spec, timeline);
                if (spec.Role == SandboxRole.Restore)
                    session.OnRun = command =>
                    {
                        onRestore?.Invoke(command);
                        WriteDerived(spec, ".tenninety/restore-packages/pkg/data.bin", "package");
                        WriteDerived(spec, "obj/project.assets.json", "assets");
                    };
                return session;
            },
        };

    private static string RestoreTarget(SandboxCommand command) =>
        Assert.Single(command.Arguments, argument =>
            argument.StartsWith("/workspace/", StringComparison.Ordinal) &&
            (argument.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
             argument.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
             argument.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
             argument.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
             argument.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)));

    private static void AssertOneRestoreTarget(SandboxCommand command)
    {
        Assert.Equal("/usr/bin/dotnet", command.Executable);
        Assert.Contains("restore", command.Arguments);
        Assert.Contains("--locked-mode", command.Arguments);
        _ = RestoreTarget(command);
    }

    private void ConfigureRestore(SandboxRestoreConfig restore)
    {
        restore.Enabled = true;
        restore.NetworkName = "tenninety-restore";
        restore.ProxyUrl = "http://restore-proxy:3128";
        restore.ApprovedFeeds =
        [
            "https://packages.example.test/v3/index.json",
            "https://mirror.example.test/v3/index.json",
        ];
        restore.Acceptance = new SandboxRestoreAcceptance
        {
            Version = SandboxRestoreAcceptance.CurrentVersion,
            Accepted = true,
            Repository = SandboxTesterGate.RepositoryIdentity(_repo.Root),
            Instance = "tenninety",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1).ToUniversalTime().ToString("O"),
            NetworkId = PreflightFakeTransport.NetworkIdFixed,
            FirewallProfile = "restore-egress-v1",
            StorageQuotaId = "restore-quota-v1",
            StorageQuotaBytes = 8L * 1024 * 1024 * 1024,
            HardQuotaEnforced = true,
            OperatorAcknowledged = true,
        };
        restore.Acceptance.FeedPolicySha256 = restore.ComputeFeedPolicySha256();
    }

    private sealed class RoleRuntime(List<string> timeline) : ISandboxRuntime
    {
        public List<SandboxSpec> Specs { get; } = [];
        public List<RecordingSandboxSession> Sessions { get; } = [];
        public Func<SandboxSpec, RecordingSandboxSession>? Factory { get; set; }

        public Task<ISandboxSession> CreateAsync(
            SandboxSpec spec, CancellationToken ct = default)
        {
            Specs.Add(spec);
            timeline.Add("create:" + spec.Role.ToString().ToLowerInvariant());
            var session = Factory?.Invoke(spec) ?? Session(spec, timeline);
            Sessions.Add(session);
            return Task.FromResult<ISandboxSession>(session);
        }
    }
}
