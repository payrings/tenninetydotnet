using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Tests for the production <see cref="DockerSandboxPreflight"/>. The fake transport routes
/// every invocation by its SHAPE (version/info/image/network/create/start/inspect/exec/stop/
/// kill/rm) and keeps a per-probe lifecycle state machine, so scripts follow the REAL
/// production call order without positional fragility: kill fallbacks and failure paths are
/// handled by state, not by handler indices.
/// </summary>
public class DockerSandboxPreflightTests : IDisposable
{
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _repo = new();
    private static readonly string CoderImageId = PreflightFakeTransport.CoderImageId;
    private static readonly string ReviewerImageId = PreflightFakeTransport.ReviewerImageId;
    private static readonly string TesterImageId = PreflightFakeTransport.TesterImageId;
    private static readonly string ProbeContainerId = PreflightFakeTransport.ProbeContainerIdFixed;

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private DockerSandboxPreflight MakePreflight(PreflightFakeTransport transport, SandboxConfig? config = null,
        Func<string, Task>? deleteOverride = null) =>
        new(new DockerCli(transport), config ?? LiveConfig(), _managedRoot.Root, _repo.Root, deleteOverride);

    private static SandboxConfig LiveConfig() => new()
    {
        Roles =
        {
            Coder = { Image = CoderImageId, Network = "model" },
            Reviewer = { Image = ReviewerImageId },
            Tester = { Image = TesterImageId },
        },
    };

    /// <summary>A config where reviewer and tester share one image and identical limits, so
    /// the coder (model network) and the offline reviewer/tester dedup into two probes.</summary>
    private static SandboxConfig TwoProbeConfig() => new()
    {
        Roles =
        {
            Coder = { Image = CoderImageId, Network = "model", Cpus = 4.0, MemoryMb = 8192, Pids = 256 },
            Reviewer = { Image = CoderImageId, Cpus = 4.0, MemoryMb = 8192, Pids = 256 },
            Tester = { Image = CoderImageId, Cpus = 4.0, MemoryMb = 8192, Pids = 256 },
        },
    };

    // ---- fixture JSON ------------------------------------------------------------------

    private static string VersionJson() =>
        "{\"Client\":{\"Version\":\"1.0\"},\"Server\":{\"Version\":\"29.0\",\"Os\":\"linux\",\"Arch\":\"amd64\"}}";

    internal static string InfoJson(string cgroupVersion = "2", string cgroupDriver = "systemd",
        string? securityOptions = "name=seccomp,profile=default\",\"name=apparmor", bool rootless = false)
    {
        var security = securityOptions is null
            ? ""
            : "\"SecurityOptions\":[\"" + securityOptions + "\"" +
              (rootless ? ",\"name=rootless\"" : "") + "],";
        if (securityOptions is null && rootless)
            security = "\"SecurityOptions\":[\"name=rootless\"],";
        return "{\"ServerVersion\":\"29.0\",\"OSType\":\"linux\",\"Architecture\":\"amd64\"," +
               "\"CgroupVersion\":\"" + cgroupVersion + "\",\"CgroupDriver\":\"" + cgroupDriver + "\"," +
               security + "\"Flags\":{}}";
    }

    private static string ImageJson(string imageId, string user = "1000:1000", string[]? digests = null) =>
        "[{\"Id\":\"" + imageId + "\",\"RepoDigests\":[" +
        string.Join(",", (digests ?? []).Select(d => "\"" + d + "\"")) +
        "],\"Config\":{\"User\":\"" + user + "\",\"Entrypoint\":[]}}]";

    internal static string StateJson(bool running) =>
        "\"State\":{\"Status\":\"" + (running ? "running" : "exited") + "\"," +
        "\"Running\":" + (running ? "true" : "false") +
        ",\"Paused\":false,\"Restarting\":false,\"OOMKilled\":false,\"Dead\":false," +
        "\"Pid\":12345,\"ExitCode\":0,\"Error\":\"\"}";

    /// <summary>Shared fixture builder used by <see cref="PreflightFakeTransport"/> so the
    /// fake emits production-shaped detailed inspect data.</summary>
    internal static string BuildDetailedInspect(
        string containerId, string imageId, string source, string networkName,
        string user = "1000:1000", long nanoCpus = 1_000_000_000L,
        long memoryBytes = 256L * 1024 * 1024, long? pids = 64,
        bool readOnly = true, string capDrop = "ALL", string capAdd = "",
        bool privileged = false,
        string securityOpt = "no-new-privileges",
        string pidMode = "", string ipcMode = "private",
        bool hasDevices = false, bool hasPortBindings = false,
        string? mountSource = null, int bindMountCount = 1,
        string tmpfs = "{\"/tmp\":\"size=512m,nosuid,nodev,noexec\",\"/home/tenninety\":\"size=256m,nosuid,nodev\"}",
        string ulimits = "[{\"Name\":\"nofile\",\"Soft\":4096,\"Hard\":8192}]",
        bool running = true)
    {
        return ProbeInspectJson(containerId, imageId, source, networkName, user, nanoCpus,
            memoryBytes, pids, readOnly, capDrop, capAdd, privileged, securityOpt, pidMode,
            ipcMode, hasDevices, hasPortBindings, mountSource, bindMountCount, tmpfs,
            ulimits, running);
    }

    /// <summary>Realistic docker inspect fixture for the detailed inspection (real top-level
    /// shape with HostConfig, Config, top-level Mounts and NetworkSettings).</summary>
    private static string ProbeInspectJson(
        string containerId, string imageId, string source, string networkName,
        string user = "1000:1000", long nanoCpus = 1_000_000_000L,
        long memoryBytes = 256L * 1024 * 1024, long? pids = 64,
        bool readOnly = true, string capDrop = "ALL", string capAdd = "",
        bool privileged = false,
        string securityOpt = "no-new-privileges",
        string pidMode = "", string ipcMode = "private",
        bool hasDevices = false, bool hasPortBindings = false,
        string? mountSource = null, int bindMountCount = 1,
        string tmpfs = "{\"/tmp\":\"size=512m,nosuid,nodev,noexec\",\"/home/tenninety\":\"size=256m,nosuid,nodev\"}",
        string ulimits = "[{\"Name\":\"nofile\",\"Soft\":4096,\"Hard\":8192}]",
        bool running = true)
    {
        var hostMounts = "";
        var effectiveMounts = "";
        for (var i = 0; i < bindMountCount; i++)
        {
            var src = mountSource ?? source;
            hostMounts += (i > 0 ? "," : "") +
                          "{\"Type\":\"bind\",\"Source\":\"" + src +
                          "\",\"Target\":\"/workspace\",\"BindOptions\":{\"Propagation\":\"rprivate\"}}";
            effectiveMounts += (i > 0 ? "," : "") +
                               "{\"Type\":\"bind\",\"Source\":\"" + src +
                               "\",\"Destination\":\"/workspace\",\"Mode\":\"\"," +
                               "\"RW\":true,\"Propagation\":\"rprivate\"}";
        }
        return "[{" +
               "\"Id\":\"" + containerId + "\",\"Image\":\"" + imageId + "\"," +
               StateJson(running) + "," +
               "\"Name\":\"/tenninety-preflight-probe\"," +
               "\"HostConfig\":{" +
               "\"NetworkMode\":\"" + networkName + "\"," +
               "\"PortBindings\":" + (hasPortBindings ? "{\"80/tcp\":null}" : "{}") + "," +
               "\"CapAdd\":[" + capAdd + "]," +
               "\"CapDrop\":[\"" + capDrop + "\"]," +
               "\"Privileged\":" + (privileged ? "true" : "false") + "," +
               "\"ReadonlyRootfs\":" + (readOnly ? "true" : "false") + "," +
               "\"SecurityOpt\":[\"" + securityOpt + "\"]," +
               "\"PidMode\":\"" + pidMode + "\"," +
               "\"IpcMode\":\"" + ipcMode + "\"," +
               "\"Devices\":" + (hasDevices ? "[{\"PathOnHost\":\"/dev/dri\"}]" : "[]") + "," +
               "\"NanoCpus\":" + nanoCpus + "," +
               "\"Memory\":" + memoryBytes + "," +
               "\"PidsLimit\":" + (pids?.ToString() ?? "null") + "," +
               "\"Ulimits\":" + ulimits + "," +
               "\"Mounts\":[" + hostMounts + "]," +
               "\"Tmpfs\":" + tmpfs + "}," +
               "\"Config\":{\"User\":\"" + user + "\",\"WorkingDir\":\"/workspace\"}," +
               "\"Mounts\":[" + effectiveMounts + "]," +
               "\"NetworkSettings\":{\"Ports\":{}}}" +
               "]";
    }

    // ---- rootful/rootless + cgroup facts -------------------------------------------------

    [Fact]
    public async Task Rootful_daemon_with_cgroup_v2_passes_the_complete_proof()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg);
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.True(report.Passed, string.Join("; ", report.Errors));
        Assert.False(report.Rootless);
        Assert.Equal("2", report.CgroupVersion);
        Assert.Equal("systemd", report.CgroupDriver);
        Assert.True(report.HasSeccomp);
        Assert.True(report.HasAppArmor);
        Assert.False(report.HasSelinux); // fixture reports no SELinux → expected warning
        Assert.Equal(CoderImageId, report.CoderImageId);
        Assert.Equal(ReviewerImageId, report.ReviewerImageId);
        Assert.Equal(TesterImageId, report.TesterImageId);
        Assert.Empty(report.Errors);
        Assert.Contains(report.Warnings, w => w.Contains("SELinux"));
        Assert.Equal(3, transport.CreatedProbes); // coder, reviewer, tester — all cleaned up
        Assert.Equal(3, transport.ProbesRemovedAndProven);
        Assert.Empty(new SandboxResourceJournal(_repo.Root).ReadAll());
        }

    [Fact]
    public async Task Coder_probe_uses_the_model_network_and_offline_probes_use_none()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg);
        await MakePreflight(transport, cfg).RunAsync();

        var coderCreate = transport.Creates[0].Arguments.ToList();
        Assert.Equal("tenninety-coder-model", coderCreate[coderCreate.IndexOf("--network") + 1]);
        Assert.Contains("tenninety.role=coder", coderCreate);
        var offlineCreate = transport.Creates[1].Arguments.ToList();
        Assert.Equal("none", offlineCreate[offlineCreate.IndexOf("--network") + 1]);
        Assert.Contains("tenninety.role=reviewer", offlineCreate);
    }

    [Fact]
    public async Task Rootless_mode_is_detected_from_structured_daemon_information()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg) { InfoJsonOverride = InfoJson(rootless: true) };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.True(report.Passed, string.Join("; ", report.Errors));
        Assert.True(report.Rootless);
    }

    [Fact]
    public async Task Cgroup_v1_with_cgroupfs_driver_is_reliable()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            InfoJsonOverride = InfoJson(cgroupVersion: "1", cgroupDriver: "cgroupfs"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.True(report.Passed, string.Join("; ", report.Errors));
        Assert.Equal("1", report.CgroupVersion);
        Assert.Equal("cgroupfs", report.CgroupDriver);
    }

    [Fact]
    public async Task Unknown_cgroup_state_fails_and_skips_all_probes()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            InfoJsonOverride = InfoJson(cgroupVersion: "", cgroupDriver: ""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("cgroup"));
        Assert.Equal(0, transport.CreatedProbes); // no probes, no cleanup calls
    }

    [Fact]
    public async Task Hostile_and_long_cgroup_values_are_never_echoed_into_preflight_errors()
    {
        // Valid Docker-info JSON shape whose CgroupVersion/CgroupDriver are hostile strings:
        // an unrelated host path, a private hostname, an opaque sensitive value and a very
        // long field. The enforcement decision is unchanged (unknown = refused), but the
        // public error must describe the values by controlled category without echoing them.
        const string pathSentinel = "/mnt/attic-vault/cgroup-host-path";
        const string hostSentinel = "cg-host-01.corp.internal";
        const string secretSentinel = "cgroup-opaque-secret-9931";
        var transport = new PreflightFakeTransport(LiveConfig())
        {
            InfoJsonOverride = InfoJson(
                cgroupVersion: pathSentinel + "; " + secretSentinel + "; " + new string('V', 500),
                cgroupDriver: hostSentinel + "; " + secretSentinel + "; " + new string('D', 500)),
        };
        var report = await MakePreflight(transport).RunAsync();

        // Fail-closed enforcement is preserved.
        Assert.False(report.Passed);
        Assert.Equal(0, transport.CreatedProbes);
        // The public error carries only controlled categories — no injected value anywhere.
        var cgroupError = report.Errors.Single(e => e.Contains("cgroup", StringComparison.Ordinal));
        Assert.Contains("cgroup enforcement cannot be relied upon", cgroupError, StringComparison.Ordinal);
        Assert.Contains("unrecognized cgroup version (value withheld)", cgroupError, StringComparison.Ordinal);
        Assert.Contains("unrecognized cgroup driver (value withheld)", cgroupError, StringComparison.Ordinal);
        Assert.DoesNotContain(pathSentinel, cgroupError, StringComparison.Ordinal);
        Assert.DoesNotContain(hostSentinel, cgroupError, StringComparison.Ordinal);
        Assert.DoesNotContain(secretSentinel, cgroupError, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('V', 100), cgroupError, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('D', 100), cgroupError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_security_options_warn_but_do_not_claim_protection()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg) { InfoJsonOverride = InfoJson(securityOptions: null) };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.True(report.Passed, string.Join("; ", report.Errors));
        Assert.False(report.HasAppArmor);
        Assert.False(report.HasSeccomp);
        Assert.False(report.HasSelinux);
        Assert.Contains(report.Warnings, w => w.Contains("AppArmor"));
        Assert.Contains(report.Warnings, w => w.Contains("seccomp"));
        Assert.Contains(report.Warnings, w => w.Contains("SELinux"));
    }

    [Fact]
    public async Task Malformed_security_options_array_members_are_hostile_and_never_claimed()
    {
        foreach (var securityJson in new[]
                 {
                     "\"name=apparmor\"",                     // not an array
                     "[\"name=apparmor\",5]",                 // non-string member
                     "[\"name=apparmor\",\"\"]",              // blank member
                     "[\"name=apparmor\",\"name=apparmor\"]", // duplicated evidence
                 })
        {
            var cfg = LiveConfig();
            var transport = new PreflightFakeTransport(cfg)
            {
                InfoJsonOverride =
                    "{\"ServerVersion\":\"29.0\",\"OSType\":\"linux\",\"Architecture\":\"amd64\"," +
                    "\"CgroupVersion\":\"2\",\"CgroupDriver\":\"systemd\",\"SecurityOptions\":" +
                    securityJson + "}",
            };
            var report = await MakePreflight(transport, cfg).RunAsync();

            Assert.True(report.Passed, string.Join("; ", report.Errors));
            Assert.False(report.HasAppArmor);
            Assert.False(report.HasSeccomp);
            Assert.False(report.Rootless);
            Assert.Contains(report.Warnings, w => w.Contains("malformed"));
        }
    }

    // ---- image verification -----------------------------------------------------------------

    [Fact]
    public async Task Local_image_mismatch_fails_preflight()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            ImageInspectOverride =
            {
                [CoderImageId] = ImageJson("sha256:" + new string('f', 64)),
            },
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        // The mismatch is reported by the controlled resolution category; the daemon/fake
        // text is never copied into preflight diagnostics.
        Assert.Contains(report.Errors, e => e.Contains("could not be resolved and verified"));
        Assert.Equal(0, transport.CreatedProbes);
    }

    [Fact]
    public async Task Registry_digest_mismatch_fails_preflight()
    {
        var cfg = LiveConfig();
        cfg.Roles.Coder.Image = "registry.example.com/team/img@sha256:" + new string('c', 64);
        var transport = new PreflightFakeTransport(cfg)
        {
            ImageInspectOverride =
            {
                [cfg.Roles.Coder.Image] = ImageJson(CoderImageId, digests:
                    ["registry.example.com/team/img@sha256:" + new string('e', 64)]),
            },
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("could not be resolved and verified"));
    }

    [Fact]
    public async Task Root_identity_image_fails_identity_verification()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            ImageUserOverride = { [CoderImageId] = "0" },
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        // The daemon-provided user text is never copied: the identity verification failure
        // is reported by its controlled category.
        Assert.Contains(report.Errors, e => e.Contains("cannot verify the coder image identity"));
        Assert.Equal(0, transport.CreatedProbes);
    }

    [Fact]
    public async Task Image_with_entrypoint_fails_the_waiting_command_prerequisite()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            ImageInspectOverride =
            {
                [CoderImageId] =
                    "[{\"Id\":\"" + CoderImageId + "\",\"RepoDigests\":[],\"Config\":{" +
                    "\"User\":\"1000:1000\",\"Entrypoint\":[\"/entrypoint.sh\"]}}]",
            },
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("ENTRYPOINT"));
    }

    // ---- networks ---------------------------------------------------------------------------

    [Fact]
    public async Task Missing_model_network_fails_preflight()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg) { MissingNetworks = { cfg.ModelNetwork } };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("does not exist"));
        Assert.Equal(0, transport.CreatedProbes);
    }

    [Fact]
    public async Task Reserved_model_network_is_rejected_without_any_network_call()
    {
        var cfg = LiveConfig();
        cfg.ModelNetwork = "host";
        var transport = new PreflightFakeTransport(cfg);
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("not a permitted Docker network"));
        Assert.Equal(0, transport.NetworkInspects.Count); // network inspect never ran
    }

    [Fact]
    public async Task Mismatched_network_inspect_result_is_an_error_not_absence()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            NetworkInspectNameOverride = "a-different-network",
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("could not be inspected"));
    }

    [Fact]
    public async Task Network_inspect_timeout_is_preserved_not_treated_as_absence()
    {
        var cfg = LiveConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            NetworkInspectTimeout = true,
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        // The timeout is an error, and absence is NEVER claimed for it.
        Assert.Contains(report.Errors, e => e.Contains("could not be inspected"));
        Assert.DoesNotContain(report.Errors, e => e.Contains("does not exist"));
    }

    [Fact]
    public async Task Enabled_restore_network_is_inspected_and_probed_with_the_restore_role()
    {
        var cfg = LiveConfig();
        cfg.Roles.Tester.Restore.Enabled = true;
        cfg.Roles.Tester.Restore.NetworkName = "tenninety-restore";
        cfg.Roles.Tester.Restore.ProxyUrl = "http://restore-proxy:3128";
        cfg.Roles.Tester.Restore.ApprovedFeeds = ["https://api.nuget.org/v3/index.json"];
        cfg.Roles.Tester.Restore.Acceptance = new SandboxRestoreAcceptance
        {
            Version = SandboxRestoreAcceptance.CurrentVersion,
            Accepted = true,
            Repository = "repository",
            Instance = "instance",
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            NetworkId = PreflightFakeTransport.NetworkIdFixed,
            FirewallProfile = "restore-egress-v1",
            StorageQuotaId = "restore-quota-v1",
            StorageQuotaBytes = 8L * 1024 * 1024 * 1024,
            HardQuotaEnforced = true,
            OperatorAcknowledged = true,
        };
        cfg.Roles.Tester.Restore.Acceptance.FeedPolicySha256 =
            cfg.Roles.Tester.Restore.ComputeFeedPolicySha256();
        var transport = new PreflightFakeTransport(cfg);

        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.True(report.Passed, string.Join("; ", report.Errors));
        Assert.Equal(["network", "inspect", "tenninety-restore"],
            transport.NetworkInspects.Single(n => n.Arguments[2] == "tenninety-restore").Arguments);
        Assert.Equal(4, transport.CreatedProbes); // coder, reviewer, tester, restore
        var restoreCreate = transport.Creates[3].Arguments.ToList();
        Assert.Equal("tenninety-restore", restoreCreate[restoreCreate.IndexOf("--network") + 1]);
        Assert.Contains("tenninety.role=restore", restoreCreate);
    }

    // ---- probe verification (effective settings) -----------------------------------------------

    [Fact]
    public async Task Probe_write_failure_fails_preflight_and_cleanup_still_proves()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            WorkspaceWriteResult = Err("permission denied"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("cannot write /workspace"));
        Assert.Equal(2, transport.CreatedProbes);
        Assert.Equal(2, transport.ProbesRemovedAndProven); // cleanup still proved for every probe
    }

    [Fact]
    public async Task Read_only_root_failure_is_detected_when_a_root_write_succeeds()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg) { RootWriteResult = Ok("") };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("not read-only"));
    }

    [Fact]
    public async Task Incorrect_effective_identity_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json => json.Replace("\"User\":\"1000:1000\"", "\"User\":\"0:0\""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("the effective container user"));
    }

    [Fact]
    public async Task Incorrect_effective_cpu_limit_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace($"\"NanoCpus\":{4000000000L}", "\"NanoCpus\":8000000000"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("CPU limit"));
    }

    [Fact]
    public async Task Incorrect_effective_memory_limit_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"Memory\":8589934592", "\"Memory\":17179869184"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("memory limit"));
    }

    [Fact]
    public async Task Incorrect_effective_network_mode_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"NetworkMode\":\"tenninety-coder-model\"", "\"NetworkMode\":\"bridge\""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("network mode"));
    }

    [Fact]
    public async Task Incorrect_mount_source_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                System.Text.RegularExpressions.Regex.Replace(
                    json, "\"Source\":\"[^\"]*\"", "\"Source\":\"/wrong/source\""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("bind source"));
    }

    [Fact]
    public async Task Two_bind_mounts_are_detected_as_a_violation()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            // Only the EFFECTIVE top-level Mounts array is mutated (anchored on the Config
            // object that immediately precedes it): a HostConfig.Mounts entry must stay
            // fully typed, and a malformed injected entry would fail at parse time instead
            // of exercising the effective-bind-count verifier.
            DetailedInspectMutator = json => json.Replace(
                "\"WorkingDir\":\"/workspace\"},\"Mounts\":[{",
                "\"WorkingDir\":\"/workspace\"},\"Mounts\":[{\"Type\":\"bind\"," +
                "\"Source\":\"/extra\",\"Destination\":\"/extra\",\"RW\":false," +
                "\"Propagation\":\"rprivate\"},{"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("exactly one bind mount"));
    }

    [Fact]
    public async Task Contradictory_extra_hostconfig_bind_entry_is_detected()
    {
        // A second, disagreeing bind representation hidden BEHIND an agreeing first entry
        // must also be caught: the cross-check checks every HostConfig bind, not only the
        // first one.
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json => json.Replace(
                "\",\"BindOptions\":{\"Propagation\":\"rprivate\"}}],\"Tmpfs\"",
                "\",\"BindOptions\":{\"Propagation\":\"rprivate\"}}," +
                "{\"Type\":\"bind\",\"Source\":\"/hostile\",\"Target\":\"/workspace\"," +
                "\"BindOptions\":{\"Propagation\":\"rprivate\"}}],\"Tmpfs\""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("contradicts"));
    }

    [Fact]
    public async Task ReadOnly_bind_mount_is_detected_as_a_violation()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json => json.Replace("\"RW\":true", "\"RW\":false"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("not writable"));
    }

    [Fact]
    public async Task Missing_security_opt_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"SecurityOpt\":[\"no-new-privileges\"]", "\"SecurityOpt\":[]"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("no-new-privileges"));
    }

    [Fact]
    public async Task Seccomp_unconfined_is_detected_as_a_violation()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"SecurityOpt\":[\"no-new-privileges\"]",
                    "\"SecurityOpt\":[\"no-new-privileges\",\"seccomp=unconfined\"]"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("seccomp=unconfined"));
    }

    [Fact]
    public async Task Capability_additions_and_privileged_mode_are_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"CapAdd\":[]", "\"CapAdd\":[\"NET_ADMIN\"]")
                    .Replace("\"Privileged\":false", "\"Privileged\":true"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("CapAdd"));
        Assert.Contains(report.Errors, e => e.Contains("privileged"));
    }

    [Fact]
    public async Task Host_pid_or_ipc_mode_and_devices_are_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"PidMode\":\"\"", "\"PidMode\":\"host\"")
                    .Replace("\"IpcMode\":\"private\"", "\"IpcMode\":\"host\"")
                    .Replace("\"Devices\":[]", "\"Devices\":[{\"PathOnHost\":\"/dev/dri\"}]"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("PID mode"));
        Assert.Contains(report.Errors, e => e.Contains("IPC mode"));
        Assert.Contains(report.Errors, e => e.Contains("devices"));
    }

    [Fact]
    public async Task Published_ports_are_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"PortBindings\":{}", "\"PortBindings\":{\"8080/tcp\":null}"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("published ports"));
    }

    [Fact]
    public async Task Missing_capability_drop_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"CapDrop\":[\"ALL\"]", "\"CapDrop\":[]"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("CapDrop ALL"));
    }

    // ---- Phase 4 follow-up regressions: null collections must parse to EMPTY and still fail
    // the real verification (a null must never bypass the effective-settings cross-check). ----

    [Fact]
    public async Task CapDrop_null_parses_to_empty_and_real_preflight_verification_fails()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"CapDrop\":[\"ALL\"]", "\"CapDrop\":null"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        // The parser normalizes the null to an empty list; the verifier must then fail the
        // probe because no capability drop is effective.
        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("CapDrop ALL"));
        // The failed probe still went through proven stop/remove/absence cleanup.
        Assert.True(transport.ProbesRemovedAndProven >= 1, "probe cleanup must be proven");
        Assert.Empty(report.Errors.Where(e => e.Contains("cleanup did not fully succeed")));
    }

    [Fact]
    public async Task SecurityOpt_null_parses_to_empty_and_real_preflight_verification_fails()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"SecurityOpt\":[\"no-new-privileges\"]", "\"SecurityOpt\":null"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        // No security option is claimed from a null; the no-new-privileges check must fail.
        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("no-new-privileges"));
        Assert.True(transport.ProbesRemovedAndProven >= 1, "probe cleanup must be proven");
        Assert.Empty(report.Errors.Where(e => e.Contains("cleanup did not fully succeed")));
    }

    [Fact]
    public async Task ReadOnly_root_flag_off_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"ReadonlyRootfs\":true", "\"ReadonlyRootfs\":false"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("read-only"));
    }

    [Fact]
    public async Task Missing_nofile_ulimit_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"Ulimits\":[{\"Name\":\"nofile\",\"Soft\":4096,\"Hard\":8192}]",
                    "\"Ulimits\":[]"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("nofile"));
    }

    [Fact]
    public async Task Wrong_tmpfs_options_are_detected_not_just_missing_keys()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace(
                    "{\"/tmp\":\"size=512m,nosuid,nodev,noexec\",\"/home/tenninety\":\"size=256m,nosuid,nodev\"}",
                    "{\"/tmp\":\"size=64m\",\"/home/tenninety\":\"size=32m\"}"),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("tmpfs"));
    }

    [Fact]
    public async Task Incorrect_workdir_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace("\"WorkingDir\":\"/workspace\"", "\"WorkingDir\":\"/\""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("work directory"));
    }

    [Fact]
    public async Task Contradictory_hostconfig_mount_representation_is_detected()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json =>
                json.Replace(
                    "\"Mounts\":[{\"Type\":\"bind\",\"Source\":\"",
                    "\"Mounts\":[{\"Type\":\"bind\",\"Source\":\"/contradictory\",\"Target\":\"/workspace\",\"BindOptions\":{\"Propagation\":\"rprivate\"}},{\"Type\":\"bind\",\"Source\":\""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("contradicts"));
    }

    [Fact]
    public async Task Malformed_hostconfig_mount_type_cannot_silently_skip_the_cross_check()
    {
        // A hostile "Type": 7 inside HostConfig.Mounts must not be rewritten into a value
        // the bind cross-check ignores: strict parsing fails the probe, container cleanup
        // is still attempted, the final removal/absence proof succeeds, and workspace
        // deletion is still attempted. (Only the HostConfig entry carries "Target", so the
        // mutation cannot touch the effective top-level Mounts representation.)
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            DetailedInspectMutator = json => System.Text.RegularExpressions.Regex.Replace(
                json,
                "\\{\"Type\":\"bind\",\"Source\":\"[^\"]*\",\"Target\":\"/workspace\"",
                "{\"Type\":7,\"Source\":\"/hostile\",\"Target\":\"/workspace\""),
        };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("probe failed"));
        Assert.Equal(2, transport.CreatedProbes);
        Assert.Equal(2, transport.ProbesRemovedAndProven); // removal/absence proven for every probe
        Assert.DoesNotContain(report.Errors, e => e.Contains("cleanup did not fully succeed"));
        Assert.Empty(Directory.GetDirectories(_managedRoot.Root, "attempt-preflight-probe-*"));
    }

    // ---- probe lifecycle failures --------------------------------------------------------------

    [Fact]
    public async Task Probe_start_failure_cleans_up_and_surfaces_the_primary_error()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg) { StartResult = Err("start failed") };
        // Only the FIRST probe start fails; the offline probe still succeeds.
        transport.StartFailureCount = 1;

        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        // The failed start is reported by controlled probe category (the fake's stderr text
        // is never copied into preflight diagnostics), and cleanup still proves removal.
        Assert.Contains(report.Errors, e => e.Contains("coder preflight probe failed"));
        // The created container was still cleaned up and absence proven.
        Assert.Equal(2, transport.CreatedProbes);
        Assert.Equal(2, transport.ProbesRemovedAndProven);
        Assert.DoesNotContain(report.Errors, e => e.Contains("cleanup did not fully succeed"));
    }

    [Fact]
    public async Task Probe_removal_failure_is_surfaced_and_fails_the_report()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            Rm = PreflightFakeTransport.RmMode.FailBusy,
            // The probe container really writes a sentinel into its mounted workspace.
            TouchHostWorkspaceOnWriteProbe = true,
        };
        var attemptedDeletions = new List<string>();
        var preflight = MakePreflight(transport, cfg, deleteOverride: path =>
        {
            attemptedDeletions.Add(path);
            return Task.CompletedTask;
        });
        var report = await preflight.RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("cleanup did not fully succeed"));
        Assert.Contains(report.Errors, e => e.Contains("remove"));

        // Removal is unproven: the exact probe workspaces and their sentinel evidence REMAIN,
        // and workspace deletion was never attempted. The probe whose execs ran wrote a real
        // sentinel into its mounted workspace; that evidence survives with the workspace.
        Assert.Empty(attemptedDeletions);
        var retained = Directory.GetDirectories(_managedRoot.Root, "attempt-preflight-probe-*");
        Assert.Equal(2, retained.Length);
        Assert.All(retained, dir => Assert.True(
            Directory.Exists(dir),
            "the probe workspace must remain while removal is unproven"));
        Assert.Single(retained.Where(dir =>
            File.Exists(System.IO.Path.Combine(dir, ".tenninety-preflight-write"))));
        var journaled = new SandboxResourceJournal(_repo.Root).ReadAll();
        Assert.Equal(2, journaled.Count);
        Assert.All(journaled, record =>
        {
            Assert.Equal("tenninety", record.Labels["tenninety.instance"]);
            Assert.Equal(
                SandboxPolicy.RepositoryIdentity(_repo.Root),
                record.Labels["tenninety.repository"]);
            Assert.NotNull(record.ContainerId);
        });
    }

    [Fact]
    public async Task Probe_create_attempted_without_a_returned_identity_retains_the_workspace()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            // The create "succeeds" on the daemon but returns a malformed id: the adapter
            // refuses it, so no container identity or removal proof ever exists.
            CreateResultOverride = "short-id",
            TouchHostWorkspaceOnWriteProbe = true,
        };
        var attemptedDeletions = new List<string>();
        var preflight = MakePreflight(transport, cfg, deleteOverride: path =>
        {
            attemptedDeletions.Add(path);
            return Task.CompletedTask;
        });
        var report = await preflight.RunAsync();

        Assert.False(report.Passed);
        // A create was ATTEMPTED without a returned identity/removal proof: the workspace is
        // conservatively retained and deletion is never attempted.
        Assert.Contains(report.Errors, e => e.Contains("probe failed"));
        Assert.Contains(report.Errors, e => e.Contains("conservatively retained"));
        Assert.Equal(2, transport.CreatedProbes);
        Assert.Empty(attemptedDeletions);
        var retained = Directory.GetDirectories(_managedRoot.Root, "attempt-preflight-probe-*");
        Assert.Equal(2, retained.Length);
        Assert.All(retained, dir => Assert.True(
            Directory.Exists(dir),
            "the probe workspace remains while removal is unproven"));
        var journaled = new SandboxResourceJournal(_repo.Root).ReadAll();
        Assert.Equal(2, journaled.Count);
        Assert.All(journaled, record => Assert.Null(record.ContainerId));
    }

    [Fact]
    public async Task Probe_still_running_after_graceful_stop_kills_then_proves_absence()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg) { PostStopInspectRunning = true };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.True(report.Passed, string.Join("; ", report.Errors));
        Assert.Contains(transport.Invocations, i => i.Arguments[0] == "kill");
        Assert.Equal(2, transport.ProbesRemovedAndProven);
    }

    [Fact]
    public async Task Probe_removal_absence_contradiction_is_surfaced()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg) { AbsenceContradiction = true };
        var report = await MakePreflight(transport, cfg).RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("cleanup did not fully succeed"));
    }

    [Fact]
    public async Task Probe_workspace_deletion_failure_is_surfaced()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg);
        var preflight = MakePreflight(transport, cfg, deleteOverride: _ =>
            Task.FromException(new IOException("workspace delete blocked")));

        var report = await preflight.RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("workspace deletion"));
    }

    [Fact]
    public async Task Connectivity_failure_throws_a_structured_error()
    {
        var transport = new PreflightFakeTransport(LiveConfig())
        {
            VersionResult = new DockerCliResult(-1, "", "", TimedOut: true, Cancelled: false,
                OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1)),
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => MakePreflight(transport).RunAsync());
        Assert.Contains("connectivity", ex.Message);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static DockerCliResult Ok(string stdout = "") =>
        new(0, stdout, "", TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Err(string stderr = "error") =>
        new(1, "", stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));
}

/// <summary>
/// Negative read-only probe classification (Section 7): the root-write probe is evidence of a
/// read-only root ONLY through a definitive, concrete nonzero exit. A timeout, a cancellation,
/// an output truncation, or an OOM kill makes the preflight report FAIL (indeterminate probe)
/// while cleanup is still attempted and proven.
/// </summary>
public class DockerSandboxPreflightNegativeProbeTests : IDisposable
{
    private readonly TempDir _managedRoot = new();
    private readonly TempDir _repo = new();
    private static readonly string CoderImageId = "sha256:" + new string('a', 64);
    private static readonly string ReviewerImageId = "sha256:" + new string('b', 64);
    private static readonly string TesterImageId = "sha256:" + new string('c', 64);

    public void Dispose()
    {
        _repo.Dispose();
        _managedRoot.Dispose();
    }

    private static SandboxConfig TwoProbeConfig() => new()
    {
        Roles =
        {
            Coder = { Image = CoderImageId, Network = "model", Cpus = 4.0, MemoryMb = 8192, Pids = 256 },
            Reviewer = { Image = CoderImageId, Cpus = 4.0, MemoryMb = 8192, Pids = 256 },
            Tester = { Image = CoderImageId, Cpus = 4.0, MemoryMb = 8192, Pids = 256 },
        },
    };

    private static DockerCliResult TimedOut() =>
        new(-1, "", "", TimedOut: true, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Cancelled() =>
        new(-1, "", "", TimedOut: false, Cancelled: true,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Truncated() =>
        new(-1, "", "", TimedOut: false, Cancelled: false,
            OutputTruncated: true, Duration: TimeSpan.FromMilliseconds(1));

    // NOTE: an OOM kill is deliberately NOT part of these indeterminate scenarios: at the
    // exec-transport layer DockerCli.ExecAsync cannot represent OOM (OomKilled is derived by
    // the SESSION inspect after a nonzero exit), so a concrete exit 137 with no flags is a
    // definitive concrete exit, not an indeterminate operational failure.

    [Theory]
    [MemberData(nameof(IndeterminateResults))]
    public async Task Indeterminate_root_write_probe_fails_the_preflight_and_cleanup_is_proven(
        string scenario, DockerCliResult indeterminate)
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg) { RootWriteResult = indeterminate };
        var preflight = new DockerSandboxPreflight(
            new DockerCli(transport), cfg, _managedRoot.Root, _repo.Root);

        var report = await preflight.RunAsync();

        // The indeterminate probe must fail the report — never silently prove read-only. The
        // error carries the controlled probe category (no arbitrary text is copied).
        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("probe failed"));
        // Cleanup must still have been attempted and proven for every created probe.
        Assert.Equal(2, transport.CreatedProbes);
        Assert.Equal(2, transport.ProbesRemovedAndProven);
    }

    public static TheoryData<string, DockerCliResult> IndeterminateResults() => new()
    {
        { "timeout", TimedOut() },
        { "cancellation", Cancelled() },
        { "truncation", Truncated() },
    };

    [Fact]
    public async Task Synthetic_negative_exit_is_not_read_only_proof()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg)
        {
            // A synthetic -1 with no flags (as an operational failure would leave behind).
            RootWriteResult = new DockerCliResult(-1, "", "operational failure",
                TimedOut: false, Cancelled: false, OutputTruncated: false,
                Duration: TimeSpan.FromMilliseconds(1)),
        };
        var preflight = new DockerSandboxPreflight(
            new DockerCli(transport), cfg, _managedRoot.Root, _repo.Root);

        var report = await preflight.RunAsync();

        Assert.False(report.Passed);
        Assert.Contains(report.Errors, e => e.Contains("synthetic exit code"));
        Assert.Equal(2, transport.ProbesRemovedAndProven);
    }

    [Fact]
    public async Task Definitive_nonzero_exit_still_proves_read_only()
    {
        var cfg = TwoProbeConfig();
        var transport = new PreflightFakeTransport(cfg); // default: concrete "Read-only file system" (exit 1)
        var preflight = new DockerSandboxPreflight(
            new DockerCli(transport), cfg, _managedRoot.Root, _repo.Root);

        var report = await preflight.RunAsync();

        Assert.True(report.Passed, string.Join("; ", report.Errors));
        Assert.Equal(2, transport.ProbesRemovedAndProven);
    }

    [Fact]
    public async Task Indeterminate_positive_probes_fail_the_preflight()
    {
        // The positive probes (workspace/tmp/home writes) also reject indeterminate results.
        foreach (var indeterminate in new[] { TimedOut(), Cancelled(), Truncated() })
        {
            var cfg = TwoProbeConfig();
            var transport = new PreflightFakeTransport(cfg) { WorkspaceWriteResult = indeterminate };
            var preflight = new DockerSandboxPreflight(
                new DockerCli(transport), cfg, _managedRoot.Root, _repo.Root);

            var report = await preflight.RunAsync();

            Assert.True(report.Errors.Count > 0, "scenario produced no errors");
            // The indeterminate positive probe is reported by the controlled probe category.
            Assert.Contains(report.Errors, e => e.Contains("probe failed"));
        }
    }
}
