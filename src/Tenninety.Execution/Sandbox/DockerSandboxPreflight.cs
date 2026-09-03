using Tenninety.Core.Models;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Docker sandbox preflight: verifies Docker client/daemon connectivity, resolves and
/// verifies every configured live role image, inspects the configured networks, detects
/// rootless mode, parses cgroup enforcement, reports LSM facts, and then runs REAL probes
/// built by the SAME typed create factory as production — one probe per distinct live
/// role configuration (Coder/Reviewer/Tester and Restore when enabled), each with a fresh
/// validated disposable workspace, the role's own image, identity, network and resource
/// limits. Every probe must prove it is running, every effective hardening setting is
/// verified from realistic inspect data, and cleanup is only proven after a final typed
/// absence confirmation: stop → inspect → kill fallback → inspect → remove → absence
/// proof → workspace deletion. A Passed report is impossible while any probe failure or
/// unproven cleanup remains. Never mutates daemon configuration, creates networks, or
/// pulls images.
/// </summary>
public sealed class DockerSandboxPreflight
{
    private readonly DockerCli _cli;
    private readonly SandboxConfig _config;
    private readonly string _managedRoot;
    private readonly string _authoritativeRepositoryPath;
    private readonly bool _ownedManagedRoot;
    private readonly Func<string, Task>? _deleteWorkspaceOverride;

    public DockerSandboxPreflight(
        DockerCli cli,
        SandboxConfig config,
        string managedRoot,
        string authoritativeRepositoryPath,
        bool ownedManagedRoot = false)
        : this(cli, config, managedRoot, authoritativeRepositoryPath,
            deleteWorkspaceOverride: null, ownedManagedRoot: ownedManagedRoot)
    {
    }

    /// <summary>Seam constructor (InternalsVisibleTo): injects a workspace-deletion delegate
    /// so tests can deterministically exercise deletion failure surfacing.</summary>
    internal DockerSandboxPreflight(
        DockerCli cli,
        SandboxConfig config,
        string managedRoot,
        string authoritativeRepositoryPath,
        Func<string, Task>? deleteWorkspaceOverride)
        : this(cli, config, managedRoot, authoritativeRepositoryPath,
            deleteWorkspaceOverride, ownedManagedRoot: false)
    {
    }

    private DockerSandboxPreflight(
        DockerCli cli,
        SandboxConfig config,
        string managedRoot,
        string authoritativeRepositoryPath,
        Func<string, Task>? deleteWorkspaceOverride,
        bool ownedManagedRoot)
    {
        _cli = cli;
        _config = config;
        _managedRoot = managedRoot;
        _authoritativeRepositoryPath = authoritativeRepositoryPath;
        _ownedManagedRoot = ownedManagedRoot;
        _deleteWorkspaceOverride = deleteWorkspaceOverride;
    }

    public async Task<DockerPreflightReport> RunAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // 1. Client + daemon connectivity with structured output (version proves the client
        //    can talk to a daemon; a missing Server object fails closed inside the parser).
        DockerDaemonInfo daemonInfo;
        bool securityOptionsMalformed;
        try
        {
            _ = await _cli.GetVersionAsync(ct);
            var facts = await _cli.GetDaemonInfoAsync(ct);
            daemonInfo = facts.Info;
            securityOptionsMalformed = facts.SecurityOptionsMalformed;
        }
        catch (Exception ex)
        {
            // Controlled category only: public preflight diagnostics never copy arbitrary
            // daemon output or exception messages/chains. The exception type name is a
            // bounded, controlled identifier that preserves the failure class.
            throw new InvalidOperationException(
                "docker client/daemon connectivity check failed (" + ex.GetType().Name + ").");
        }

        // 2. Cgroup enforcement: fail live execution when CPU/memory/PID limits cannot be
        //    relied upon. Missing or unknown data is never treated as enforcement. The error
        //    is composed from CONTROLLED categories only: recognized cgroup versions/drivers
        //    map to fixed allowlisted labels, and unknown/missing/malformed daemon values are
        //    described by category WITHOUT echoing the original text (typed daemon facts are
        //    arbitrary strings — parsing them does not make them safe to publish).
        if (!daemonInfo.CgroupEnforcementReliable)
            errors.Add(
                "cgroup enforcement cannot be relied upon (cgroup version: " +
                DescribeCgroupVersionCategory(daemonInfo.CgroupVersion) + ", driver: " +
                CgroupDriverCategory(daemonInfo.CgroupDriver) + "); CPU, memory and PID " +
                "limits may not be effective. Live docker execution is refused.");

        // 3. LSM/security facts: report exactly what inspect evidence shows; never claim an
        //    LSM is enabled without evidence. Malformed SecurityOptions are hostile output.
        if (securityOptionsMalformed)
        {
            warnings.Add(
                "docker info returned SecurityOptions in a malformed shape; no security option " +
                "is claimed and reduced protection is assumed.");
        }
        else
        {
            if (!daemonInfo.HasAppArmor)
                warnings.Add("AppArmor is not reported by the Docker daemon; reduced host protection.");
            if (!daemonInfo.HasSeccomp)
                warnings.Add("seccomp is not reported by the Docker daemon; reduced host protection.");
            if (!daemonInfo.HasSelinux)
                warnings.Add("SELinux is not reported by the Docker daemon; reduced host protection.");
        }

        // 4. Resolve every configured live role image and verify identity + waiting-command
        //    prerequisites for EVERY distinct image.
        var coderImage = await ResolveImageAsync(_config.Roles.Coder.Image, "coder", errors, ct);
        var reviewerImage = await ResolveImageAsync(_config.Roles.Reviewer.Image, "reviewer", errors, ct);
        var testerImage = await ResolveImageAsync(_config.Roles.Tester.Image, "tester", errors, ct);
        foreach (var (info, role) in new[]
                 {
                     (coderImage, "coder"), (reviewerImage, "reviewer"), (testerImage, "tester"),
                 })
        {
            if (info is null) continue;
            try
            {
                var identity = ContainerIdentity.Parse(info.ConfiguredUser);
                if (identity.IsRoot)
                    errors.Add($"the {role} image is configured as root (uid=0); sandbox containers " +
                               "must run as a non-root numeric identity.");
            }
            catch (Exception ex)
            {
                errors.Add($"cannot verify the {role} image identity ({ex.GetType().Name}).");
            }
            if (info.ConfigEntrypoint.Count > 0)
                errors.Add($"the {role} image declares an ENTRYPOINT; the fixed waiting command " +
                           "cannot be guaranteed.");
        }

        // 5. Networks: exact configured names only. Reserved/invalid names fail without any
        //    daemon call; a missing network, a mismatched inspect result, or an operational
        //    inspect failure is an error — never silently treated as absent. INVALID
        //    configured names are described by category (a raw invalid configuration value is
        //    never echoed into public diagnostics); VALIDATED names are bounded, non-secret
        //    identifiers and may be named where useful.
        if (!Tenninety.Core.Models.SandboxConfig.IsValidDockerNetworkName(_config.ModelNetwork))
            errors.Add(
                "sandbox.model_network is not a permitted Docker network name (the invalid " +
                "value is withheld); reserved networks (host, bridge, none, default) are " +
                "never permitted.");
        else
        {
            var modelNetwork = await InspectNetworkOrDefaultAsync(_config.ModelNetwork, "model", errors, ct);
            if (modelNetwork is { IsReserved: true })
                errors.Add($"the model network '{_config.ModelNetwork}' resolved to a reserved network.");
        }

        if (_config.Roles.Tester.Restore.Enabled)
        {
            var restoreName = _config.Roles.Tester.Restore.NetworkName;
            if (string.IsNullOrWhiteSpace(restoreName) ||
                !Tenninety.Core.Models.SandboxConfig.IsValidDockerNetworkName(restoreName))
                errors.Add(
                    "sandbox.roles.tester.restore.network_name is not a permitted Docker " +
                    "network name (the invalid value is withheld); reserved networks are " +
                    "never permitted.");
            else
            {
                var restoreNetwork = await InspectNetworkOrDefaultAsync(restoreName, "restore", errors, ct);
                if (restoreNetwork is { IsReserved: true })
                    errors.Add($"the restore network '{restoreName}' resolved to a reserved network.");
                if (restoreNetwork is not null &&
                    !string.Equals(
                        restoreNetwork.Id,
                        _config.Roles.Tester.Restore.Acceptance.NetworkId,
                        StringComparison.Ordinal))
                    errors.Add(
                        "the inspected restore network ID does not match the versioned " +
                        "operator acceptance record; Restore is refused.");
            }
        }

        // 6. Live probes with the production hardening factory — one per distinct live role
        //    configuration (only when no error so far).
        if (errors.Count == 0)
        {
            var probes = BuildProbePlan(coderImage, reviewerImage, testerImage, errors);
            foreach (var probe in probes)
                await RunProbeAsync(probe, errors, ct);
        }

        return new DockerPreflightReport(
            daemonInfo,
            Passed: errors.Count == 0,
            Rootless: daemonInfo.Rootless,
            CgroupVersion: daemonInfo.CgroupVersion,
            CgroupDriver: daemonInfo.CgroupDriver,
            HasAppArmor: daemonInfo.HasAppArmor,
            HasSeccomp: daemonInfo.HasSeccomp,
            HasSelinux: daemonInfo.HasSelinux,
            CoderImageId: coderImage?.ImageId,
            ReviewerImageId: reviewerImage?.ImageId,
            TesterImageId: testerImage?.ImageId,
            Errors: errors,
            Warnings: warnings);
    }

    private async Task<DockerImageInfo?> ResolveImageAsync(
        string imageRef, string role, List<string> errors, CancellationToken ct)
    {
        try
        {
            return await _cli.InspectImageAsync(imageRef, ct);
        }
        catch (Exception ex)
        {
            errors.Add($"the {role} image could not be resolved and verified " +
                       $"({ex.GetType().Name}).");
            return null;
        }
    }

    // ---- controlled daemon-fact categories ------------------------------------------------

    /// <summary>Fixed allowlisted label for a RECOGNIZED cgroup version; unknown, missing or
    /// malformed daemon values are described by category WITHOUT echoing the original text.
    /// Parsed daemon facts are arbitrary strings — the type does not make them safe.</summary>
    internal static string DescribeCgroupVersionCategory(string raw) => raw switch
    {
        "1" => "cgroup v1",
        "2" => "cgroup v2",
        "" => "not reported by the daemon",
        _ => "unrecognized cgroup version (value withheld)",
    };

    /// <summary>Fixed allowlisted label for a RECOGNIZED cgroup driver; unknown, missing or
    /// malformed daemon values are described by category WITHOUT echoing the original text.</summary>
    internal static string CgroupDriverCategory(string raw) => raw switch
    {
        "systemd" => "systemd",
        "cgroupfs" => "cgroupfs",
        "" => "not reported by the daemon",
        _ => "unrecognized cgroup driver (value withheld)",
    };

    private async Task<DockerNetworkInfo?> InspectNetworkOrDefaultAsync(
        string name, string what, List<string> errors, CancellationToken ct)
    {
        try
        {
            var info = await _cli.InspectNetworkAsync(name, ct);
            if (info is null)
                errors.Add($"the {what} network '{name}' does not exist; create the exact " +
                           "configured network before live execution.");
            return info;
        }
        catch (Exception ex)
        {
            errors.Add($"the {what} network '{name}' could not be inspected " +
                       $"({ex.GetType().Name}).");
            return null;
        }
    }

    // ---- probe plan ---------------------------------------------------------------

    private sealed record ProbePlan(
        SandboxRole Role,
        DockerImageInfo Image,
        string ImageReference,
        double Cpus,
        int MemoryMb,
        int Pids,
        string NetworkName);

    /// <summary>Builds one probe per live role configuration: the Coder with its model
    /// network and limits, the Reviewer and Tester offline with their own limits, and the
    /// Restore phase (when enabled) over the tester image with the validated restore
    /// network. Dedup happens only when the full tuple — resolved image ID, numeric
    /// identity, network, and resources — is identical.</summary>
    private List<ProbePlan> BuildProbePlan(
        DockerImageInfo? coderImage,
        DockerImageInfo? reviewerImage,
        DockerImageInfo? testerImage,
        List<string> errors)
    {
        var plans = new List<ProbePlan>();

        void Add(SandboxRole role, SandboxNetworkPolicy policy, DockerImageInfo? image,
            string imageReference, double cpus, int memoryMb, int pids)
        {
            if (image is null) return;
            var identity = ContainerIdentity.Parse(image.ConfiguredUser);
            if (identity.IsRoot) return; // already recorded as an error above
            // The network is resolved by the SAME factory path production uses.
            var networkName = DockerCreateRequest.ResolveNetworkName(role, policy, _config);
            if (plans.Any(p =>
                    string.Equals(p.Image.ImageId, image.ImageId, StringComparison.Ordinal) &&
                    string.Equals(p.Image.ConfiguredUser, image.ConfiguredUser, StringComparison.Ordinal) &&
                    string.Equals(p.NetworkName, networkName, StringComparison.Ordinal) &&
                    p.Cpus.Equals(cpus) && p.MemoryMb == memoryMb && p.Pids == pids))
                return;
            plans.Add(new ProbePlan(role, image, imageReference, cpus, memoryMb, pids, networkName));
        }

        if (errors.Count == 0)
        {
            var coder = _config.Roles.Coder;
            Add(SandboxRole.Coder, SandboxNetworkPolicy.Model, coderImage, coder.Image,
                coder.Cpus, coder.MemoryMb, coder.Pids);
            var reviewer = _config.Roles.Reviewer;
            Add(SandboxRole.Reviewer, SandboxNetworkPolicy.None, reviewerImage, reviewer.Image,
                reviewer.Cpus, reviewer.MemoryMb, reviewer.Pids);
            var tester = _config.Roles.Tester;
            Add(SandboxRole.Tester, SandboxNetworkPolicy.None, testerImage, tester.Image,
                tester.Cpus, tester.MemoryMb, tester.Pids);
            if (_config.Roles.Tester.Restore.Enabled && testerImage is not null)
            {
                Add(SandboxRole.Restore, SandboxNetworkPolicy.Restore, testerImage, tester.Image,
                    tester.Cpus, tester.MemoryMb, tester.Pids);
            }
        }
        return plans;
    }

    // ---- live probe -----------------------------------------------------------------

    private async Task RunProbeAsync(ProbePlan probe, List<string> errors, CancellationToken ct)
    {
        var role = probe.Role.ToString().ToLowerInvariant();
        var attemptId = Guid.NewGuid().ToString("N")[..12];
        var probeDir = Path.Combine(
            _managedRoot, $"attempt-preflight-probe-{role}-{Guid.NewGuid():N}");
        SandboxAttemptOwnership? ownership = null;
        string? containerId = null;
        var createAttempted = false;
        var cleanupFailures = new List<string>();
        var cleanupProven = false;

        try
        {
            Directory.CreateDirectory(probeDir);
            var labels = BuildProbeLabels(role, attemptId);
            ownership = new SandboxAttemptOwnership(
                _authoritativeRepositoryPath, _managedRoot, _ownedManagedRoot, labels);
            ownership.RecordAttempt(probeDir);

            // The workspace is validated to build the spec, and revalidated AGAIN
            // immediately before the create call — no await in between.
            var spec = BuildProbeSpec(probe, probeDir, labels);
            var evidence = spec.ValidateAndCapture();
            var identity = ContainerIdentity.Parse(probe.Image.ConfiguredUser);

            var revalidated = ValidatedSandboxWorkspacePath.Create(
                probeDir, _managedRoot, _authoritativeRepositoryPath).Value;
            var request = DockerCreateRequest.FromSpec(
                evidence, _config, probe.Image.ImageId, identity,
                $"tenninety-preflight-{probe.Role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}",
                revalidated);
            createAttempted = true;
            containerId = await _cli.CreateContainerAsync(request, ct);
            ownership.SetContainer(containerId);

            try
            {
                await _cli.StartContainerAsync(containerId, ct);

                // Prove the probe is actually running (image identity verified) before exec.
                var state = await _cli.InspectContainerAsync(
                    containerId, expectedImageId: probe.Image.ImageId, ct);
                if (!state.Running)
                    throw new InvalidOperationException(
                        "the probe container is not running after start.");

                // Verify the EFFECTIVE settings from realistic inspect data.
                var detailed = await _cli.InspectContainerDetailedAsync(containerId, ct);
                VerifyProbeInspection(detailed, probe, identity, revalidated, containerId, errors);

                if (errors.Count == 0)
                    await RunProbeExecsAsync(containerId, errors, ct);
            }
            finally
            {
                // Stop, confirm stopped, remove, and verify absence — surfacing every failure.
                // Covers the start-failure case too: a created container is always cleaned up.
                await CleanupProbeAsync(containerId, cleanupFailures, () =>
                {
                    ownership?.ContainerRemoved();
                    cleanupProven = true;
                });
            }
        }
        catch (Exception ex)
        {
            // Controlled category only — the probe failure is reported by stage and
            // exception type, never by arbitrary daemon/exception text.
            errors.Add($"the {role} preflight probe failed ({ex.GetType().Name}).");
        }
        finally
        {
            // The probe workspace is deleted ONLY when no container may still be writing it:
            // removal must be proven first (or no container may ever have been created).
            if (cleanupProven || (!createAttempted && containerId is null))
            {
                try
                {
                    await TrustedWorkspaceDeletion.DeleteAsync(probeDir, _managedRoot, _deleteWorkspaceOverride);
                    ownership?.CompleteAfterWorkspaceDeletion();
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(
                        "workspace deletion failed; the probe workspace '" +
                        Path.GetFileName(probeDir) + "' is retained (" + ex.GetType().Name + ")");
                }
            }
            else
            {
                cleanupFailures.Add(
                    "the probe workspace '" + Path.GetFileName(probeDir) +
                    "' is conservatively retained because container removal could not be proven");
            }

            if (!cleanupProven && containerId is not null)
                cleanupFailures.Add("remove: probe removal and absence were not proven");
            foreach (var failure in cleanupFailures)
                errors.Add($"{role} probe cleanup did not fully succeed: " + failure);
        }
    }


    /// <summary>Provable cleanup: graceful stop → inspect → kill fallback when the stop
    /// failed or the container remains running → inspect again → remove (which itself
    /// performs and requires a final typed absence confirmation) → workspace deletion is
    /// handled by the caller. Every failure is collected; nothing is swallowed.</summary>
    private async Task CleanupProbeAsync(
        string containerId, List<string> cleanupFailures, Action markProven)
    {
        Exception? stopError = null;
        try
        {
            await _cli.StopContainerAsync(containerId, TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception ex)
        {
            stopError = ex;
        }

        var stillRunning = false;
        try
        {
            var state = await _cli.TryInspectContainerAsync(containerId, CancellationToken.None);
            stillRunning = state is { Running: true };
        }
        catch (Exception ex)
        {
            cleanupFailures.Add("inspect after stop: failed (" + ex.GetType().Name + ")");
        }
        if (stopError is not null)
            cleanupFailures.Add("stop: failed (" + stopError.GetType().Name + ")");

        if (stopError is not null || stillRunning)
        {
            try
            {
                await _cli.KillContainerAsync(containerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                cleanupFailures.Add("kill: failed (" + ex.GetType().Name + ")");
            }
            try
            {
                var state = await _cli.TryInspectContainerAsync(containerId, CancellationToken.None);
                if (state is { Running: true })
                    cleanupFailures.Add("kill: the probe container is still running after the kill fallback");
            }
            catch (Exception ex)
            {
                cleanupFailures.Add("inspect after kill: failed (" + ex.GetType().Name + ")");
            }
        }

        try
        {
            // RemoveContainerAsync performs and REQUIRES the final typed absence proof;
            // a contradiction or operational failure throws and is collected below.
            await _cli.RemoveContainerAsync(containerId, CancellationToken.None);
            markProven();
        }
        catch (Exception ex)
        {
            cleanupFailures.Add("remove: failed (" + ex.GetType().Name + ")");
        }
    }

    /// <summary>Builds a VALID probe spec for the role: the role's own image, network
    /// policy, and configured limits, complete unique management labels, and the fresh
    /// disposable probe workspace. The spec passes the full SandboxSpec validation —
    /// no bypass exists.</summary>
    private SandboxSpec BuildProbeSpec(
        ProbePlan probe,
        string probeDir,
        IReadOnlyDictionary<string, string> labels)
    {
        var role = probe.Role;
        return new SandboxSpec
        {
            Role = role,
            Image = probe.ImageReference,
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                probeDir, _managedRoot, _authoritativeRepositoryPath),
            Network = role switch
            {
                SandboxRole.Coder => SandboxNetworkPolicy.Model,
                SandboxRole.Restore => SandboxNetworkPolicy.Restore,
                _ => SandboxNetworkPolicy.None,
            },
            Cpus = probe.Cpus,
            MemoryMb = probe.MemoryMb,
            Pids = probe.Pids,
            Timeout = TimeSpan.FromMinutes(5),
            Labels = labels,
        };
    }

    private IReadOnlyDictionary<string, string> BuildProbeLabels(
        string role, string attemptId) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenninety.instance"] = "tenninety",
            ["tenninety.repository"] =
                SandboxPolicy.RepositoryIdentity(_authoritativeRepositoryPath),
            ["tenninety.run"] = "preflight-" + attemptId,
            ["tenninety.wp"] = "PREFLIGHT-" + role.ToUpperInvariant(),
            ["tenninety.attempt"] = attemptId,
            ["tenninety.role"] = role,
        };

    private static bool NoNewPrivilegesEffective(IReadOnlyList<string> securityOpt)
    {
        // Docker's canonical inspect representation is "no-new-privileges"; older daemons
        // store "no-new-privileges:true" or "=true". Any negated form is not effective.
        return securityOpt.Any(o =>
            o.Equals("no-new-privileges", StringComparison.OrdinalIgnoreCase) ||
            o.StartsWith("no-new-privileges:true", StringComparison.OrdinalIgnoreCase) ||
            o.StartsWith("no-new-privileges=true", StringComparison.OrdinalIgnoreCase));
    }

    private static void VerifyProbeInspection(
        DockerContainerDetailed probe,
        ProbePlan plan,
        ContainerIdentity identity,
        string expectedSource,
        string containerId,
        List<string> errors)
    {
        var role = plan.Role.ToString().ToLowerInvariant();
        void Fail(string detail) => errors.Add($"{role} probe inspection: " + detail);

        if (!string.Equals(probe.ContainerId, containerId, StringComparison.Ordinal))
            Fail("inspected container id does not match the created container.");
        if (!string.Equals(probe.ImageId, plan.Image.ImageId, StringComparison.Ordinal))
            Fail("the probe is not running the exact resolved image id.");
        if (!probe.Running)
            Fail("the probe is not running.");
        if (!string.Equals(probe.User, identity.ToUserFlag(), StringComparison.Ordinal))
            Fail("the effective container user is not the verified numeric non-root " +
                 "identity (daemon value withheld).");
        if (!probe.ReadonlyRootfs)
            Fail("the probe root filesystem is not read-only.");
        if (!probe.CapDrop.Contains("ALL", StringComparer.Ordinal))
            Fail("capabilities were not dropped (CapDrop ALL missing).");
        if (probe.CapAdd.Count > 0)
            Fail("capabilities were added back (CapAdd must be empty).");
        if (probe.Privileged)
            Fail("the probe is privileged, which is never permitted.");
        if (!NoNewPrivilegesEffective(probe.SecurityOpt))
            Fail("no-new-privileges security option is not effective.");
        if (probe.SecurityOpt.Any(o => o.Contains("seccomp=unconfined", StringComparison.OrdinalIgnoreCase)))
            Fail("seccomp=unconfined is present; the default seccomp profile must stay effective.");
        if (probe.PidMode.Equals("host", StringComparison.OrdinalIgnoreCase))
            Fail("PID mode is host, which is never permitted.");
        if (probe.IpcMode.Equals("host", StringComparison.OrdinalIgnoreCase))
            Fail("IPC mode is host, which is never permitted.");
        if (probe.DeviceCount != 0)
            Fail($"devices are attached ({probe.DeviceCount}); sandbox containers must have none.");
        if (probe.PortBindingCount != 0)
            Fail($"published ports are present ({probe.PortBindingCount}); they are never permitted.");
        if (probe.NanoCpus != (long)(plan.Cpus * 1_000_000_000L))
            Fail($"effective CPU limit is {probe.NanoCpus} nanocpus, not the requested value.");
        if (probe.MemoryBytes != (long)plan.MemoryMb * 1024 * 1024)
            Fail($"effective memory limit is {probe.MemoryBytes} bytes, not the requested value.");
        if (probe.PidsLimit != plan.Pids)
            Fail($"effective PID limit is {probe.PidsLimit}, not the requested value.");
        if (!probe.Ulimits.Any(u => u.Name == "nofile" && u.Soft == 4096 && u.Hard == 8192))
            Fail("the fixed nofile ulimit (4096:8192) is not effective.");
        if (!string.Equals(probe.NetworkMode, plan.NetworkName, StringComparison.Ordinal))
            Fail("the effective network mode is not the required configured network " +
                 "(daemon value withheld).");

        // Exactly one WRITABLE bind mount at /workspace from the exact revalidated source;
        // a second bind is a violation regardless of its target.
        var binds = probe.Mounts.Where(m => m.Type == "bind").ToList();
        if (binds.Count != 1)
            Fail($"exactly one bind mount is required but {binds.Count} are present.");
        var bind = binds.FirstOrDefault();
        if (bind is not null)
        {
            if (bind.Destination != SandboxPolicy.ContainerWorkspacePath)
                Fail($"the bind destination is not {SandboxPolicy.ContainerWorkspacePath} " +
                     "(daemon value withheld).");
            if (bind.Source != expectedSource)
                Fail("the bind source does not equal the freshly revalidated workspace path.");
            if (bind.Rw != true)
                Fail("the workspace bind mount is not writable (RW must be true).");
            if (bind.Propagation != "rprivate")
                Fail("the bind propagation is not rprivate (daemon value withheld).");
        }

        // HostConfig.Mounts cross-check when present: EVERY bind representation must agree
        // with the effective top-level mount — checking only the first would let a
        // contradictory extra entry hide behind an agreeing one. The parser guarantees
        // each present entry is fully strictly typed, so malformed HostConfig data fails
        // the probe instead of making this cross-check silently disappear.
        foreach (var hostBind in probe.HostMounts.Where(m => m.Type == "bind"))
        {
            if (hostBind.Source != expectedSource ||
                hostBind.Target != SandboxPolicy.ContainerWorkspacePath ||
                hostBind.Propagation != "rprivate")
                Fail("the HostConfig.Mounts representation contradicts the effective top-level mount.");
        }

        // Exact bounded tmpfs options (not merely key presence).
        foreach (var expected in SandboxPolicy.FixedTmpfsMounts)
        {
            if (!probe.Tmpfs.TryGetValue(expected.ContainerPath, out var options) ||
                !string.Equals(options, expected.Options, StringComparison.Ordinal))
                Fail($"the tmpfs mount at '{expected.ContainerPath}' is missing or its exact " +
                     "expected bounded options are not effective (daemon value withheld).");
        }
        if (probe.Tmpfs.Keys.Any(k =>
                k != "/tmp" && k != SandboxPolicy.ContainerHomePath))
            Fail("an unexpected extra tmpfs mount is present.");

        if (!string.Equals(probe.WorkingDir, SandboxPolicy.ContainerWorkspacePath, StringComparison.Ordinal))
            Fail($"the work directory is not {SandboxPolicy.ContainerWorkspacePath} " +
                 "(daemon value withheld).");
    }

    private async Task RunProbeExecsAsync(string containerId, List<string> errors, CancellationToken ct)
    {
        async Task<SandboxCommandResult> Exec(string executable, string arg)
        {
            var command = new SandboxCommand
            {
                Executable = executable,
                Arguments = [arg],
                WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
                StdIn = null,
                Timeout = TimeSpan.FromSeconds(15),
                MaxOutputBytes = 65536,
                Environment = new Dictionary<string, string>(),
            };
            return await _cli.ExecAsync(
                DockerExecRequest.FromCommand(containerId, command, command.Timeout ?? TimeSpan.FromSeconds(15)), ct);
        }

        // Every preflight exec must complete DEFINITIVELY: a timeout, a cancellation, an
        // output truncation, or an OOM kill is a preflight error — never evidence.
        SandboxCommandResult RequireDefinitive(SandboxCommandResult result, string probe)
        {
            if (result.TimedOut)
                throw new InvalidOperationException(
                    $"{probe} probe timed out; the preflight result is indeterminate.");
            if (result.Cancelled)
                throw new InvalidOperationException(
                    $"{probe} probe was cancelled; the preflight result is indeterminate.");
            if (result.OutputTruncated)
                throw new InvalidOperationException(
                    $"{probe} probe output was truncated; the preflight result is indeterminate.");
            if (result.OomKilled)
                throw new InvalidOperationException(
                    $"{probe} probe was OOM-killed; the preflight result is indeterminate.");
            return result;
        }

        var writeWorkspace = RequireDefinitive(
            await Exec("touch", "/workspace/.tenninety-preflight-write"), "workspace write");
        if (!writeWorkspace.Succeeded)
            errors.Add("write probe: the container identity cannot write /workspace " +
                       $"(exit {writeWorkspace.ExitCode}).");

        // The read-only negative probe is successful ONLY through a definitive, concrete,
        // nonzero exit: the synthetic -1 used for operational failures is never proof of a
        // read-only filesystem.
        var writeRoot = RequireDefinitive(
            await Exec("touch", "/.tenninety-preflight-ro-probe"), "read-only root");
        if (writeRoot.ExitCode == 0)
            errors.Add("read-only probe: a root-filesystem write unexpectedly succeeded; " +
                       "the container root is not read-only.");
        else if (writeRoot.ExitCode < 0)
            errors.Add($"read-only probe: the root write failed with a synthetic exit code " +
                       $"{writeRoot.ExitCode}; the read-only state could not be proven.");

        var writeTmp = RequireDefinitive(
            await Exec("touch", "/tmp/.tenninety-preflight-tmp"), "tmp write");
        if (!writeTmp.Succeeded)
            errors.Add("tmp probe: /tmp is not writable (the bounded tmpfs must be writable).");

        var writeHome = RequireDefinitive(
            await Exec("touch", SandboxPolicy.ContainerHomePath + "/.tenninety-preflight-home"),
            "home write");
        if (!writeHome.Succeeded)
            errors.Add("home probe: the isolated HOME is not writable (the bounded tmpfs must be writable).");
    }
}

/// <summary>
/// Typed preflight report with daemon facts and explicit errors/warnings. Passed is true
/// only when no error exists — including unconfirmed stop state, unproven removal/absence,
/// workspace deletion failures, and any role probe failure.
/// </summary>
public sealed record DockerPreflightReport(
    DockerDaemonInfo DaemonInfo,
    bool Passed,
    bool Rootless,
    string CgroupVersion,
    string CgroupDriver,
    bool HasAppArmor,
    bool HasSeccomp,
    bool HasSelinux,
    string? CoderImageId,
    string? ReviewerImageId,
    string? TesterImageId,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool IsReady => Passed && Errors.Count == 0;
}
