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
        File.WriteAllText(Path.Combine(_repo.Root, ".gitignore"), ".tenninety/\n");
        File.WriteAllText(Path.Combine(_repo.Root, "tests.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" />" +
            "</ItemGroup></Project>");
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
        Assert.Equal(
            ["restore", "--locked-mode", "--configfile",
             "/workspace/.tenninety/restore-control/NuGet.Config", "--packages",
             "/workspace/.tenninety/restore-packages", "--nologo"],
            restoreCommand.Arguments);
        Assert.Contains("<clear", controlXml);
        Assert.Contains("https://packages.example.test/v3/index.json", controlXml);
        Assert.Contains("https://mirror.example.test/v3/index.json", controlXml);
        Assert.DoesNotContain("api.nuget.org", controlXml);
        Assert.True(transport.Disposed);
        Assert.Equal("candidate\n", File.ReadAllText(Path.Combine(_repo.Root, "source.txt")));
        Assert.Equal(_candidateSha, _git.HeadSha());
        Assert.Empty(Directory.GetFileSystemEntries(_managedRoot.Root));
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

    private TesterRunContext Context() => new()
    {
        Candidate = new CandidateRevision("main", _candidateSha, _candidateSha),
        WorkPackageId = "WP-001",
        Attempt = 1,
    };

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
