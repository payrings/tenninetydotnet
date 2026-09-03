using System.Collections.Frozen;
using Tenninety.Core.Models;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Typed create request for one hardened sandbox container. INTERNAL and constructible ONLY
/// through <see cref="FromSpec"/>, which requires a <see cref="SandboxSpecEvidence"/> — verifiable
/// proof that a complete <see cref="SandboxSpec.Validate()"/> succeeded — so no caller can build
/// a request that bypasses spec validation. Every collection is a defensive immutable snapshot
/// and every property is read-only; the fixed policy values (workspace target, tmpfs mounts,
/// waiting command) are never parameters. <see cref="DockerCli"/> owns the argument vector built
/// from this request; runtime, session, and preflight never construct raw Docker vectors.
/// </summary>
internal sealed class DockerCreateRequest
{
    public string ContainerName { get; }
    /// <summary>Exact inspected local image ID (sha256:<64 lowercase hex>).</summary>
    public string ExactImageId { get; }
    public ContainerIdentity User { get; }
    /// <summary>`none` or a validated pre-existing network name; never host/bridge/default.</summary>
    public string NetworkName { get; }
    public double Cpus { get; }
    public int MemoryMb { get; }
    public int Pids { get; }
    /// <summary>Exactly the fixed bounded tmpfs mounts from <see cref="SandboxPolicy"/>.</summary>
    public IReadOnlyList<TmpfsMount> Tmpfs { get; }
    /// <summary>Complete validated Tenninety management identity labels (immutable snapshot).</summary>
    public IReadOnlyDictionary<string, string> Labels { get; }
    /// <summary>Role-allowlisted environment only (immutable snapshot).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }
    /// <summary>Freshly revalidated disposable workspace source (comma-free).</summary>
    public string WorkspaceSource { get; }
    /// <summary>Always exactly the fixed /workspace target.</summary>
    public string ContainerWorkspaceTarget { get; }
    /// <summary>Always exactly `sleep infinity` as literal container arguments.</summary>
    public IReadOnlyList<string> WaitingCommand { get; }

    /// <summary>The fixed bounded nofile ulimit soft:hard values.</summary>
    public const string NoFileUlimit = "nofile=4096:8192";

    private DockerCreateRequest(
        string containerName,
        string exactImageId,
        ContainerIdentity user,
        string networkName,
        double cpus,
        int memoryMb,
        int pids,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> environment,
        string workspaceSource)
    {
        ContainerName = containerName;
        ExactImageId = exactImageId;
        User = user;
        NetworkName = networkName;
        Cpus = cpus;
        MemoryMb = memoryMb;
        Pids = pids;
        Tmpfs = SandboxPolicy.FixedTmpfsMounts;
        Labels = labels;
        Environment = environment;
        WorkspaceSource = workspaceSource;
        ContainerWorkspaceTarget = SandboxPolicy.ContainerWorkspacePath;
        WaitingCommand = ["sleep", "infinity"];
    }

    /// <summary>
    /// Builds the create request from a VALIDATED spec. <paramref name="evidence"/> is the
    /// token issued by <see cref="SandboxSpec.ValidateAndCapture"/>; without it the request
    /// cannot exist. The factory OWNS the network resolution: callers never pass a network
    /// name — the exact (role, policy) tuple from the validated evidence plus the trusted
    /// configuration decides it, so host/bridge/default, arbitrary names and mismatched
    /// role-policy combinations are unreachable. The caller must already have resolved and
    /// verified the image and identity, and revalidated the workspace immediately before
    /// calling this (no awaits in between).
    /// </summary>
    internal static DockerCreateRequest FromSpec(
        SandboxSpecEvidence evidence,
        SandboxConfig config,
        string exactImageId,
        ContainerIdentity identity,
        string containerName,
        string revalidatedWorkspaceSource)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(identity);

        // Identity: verified numeric non-root only.
        if (identity.IsRoot)
            throw new InvalidOperationException(
                "create request identity is root (uid=0); sandbox containers must run as a " +
                "non-root numeric identity.");

        // Network: resolved INSIDE the factory from the exact validated tuple.
        var networkName = ResolveNetworkName(evidence.Role, evidence.Network, config);

        // Workspace source: mount-grammar-safe.
        DockerValidation.RequireMountSource(revalidatedWorkspaceSource, "create request workspace source");
        DockerValidation.RequireContainerName(containerName, "create request container name");
        DockerValidation.RequireImageId(exactImageId, "create request image id");

        // Resources: validated bounds re-checked from the evidence.
        if (evidence.Cpus is double.NaN or double.PositiveInfinity or <= 0 or > 256)
            throw new InvalidOperationException("create request has invalid Cpus.");
        if (evidence.MemoryMb is < 128 or > 1_048_576)
            throw new InvalidOperationException("create request has invalid MemoryMb.");
        if (evidence.Pids is < 1 or > 32_768)
            throw new InvalidOperationException("create request has invalid Pids.");

        // Labels: the COMPLETE validated Tenninety management identity (same rules as the
        // scoped list), snapshotted so later mutation of any source dictionary is inert.
        var labels = ValidateAndSnapshotLabels(evidence.Labels, "create request labels");

        // Environment: only the role's closed allowlist, snapshotted.
        var permittedEnvironment = SandboxPolicy.PermittedEnvironmentKeys(evidence.Role);
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in evidence.Environment)
        {
            if (!permittedEnvironment.Contains(key))
                throw new InvalidOperationException(
                    $"create request environment key '{key}' is not on the closed per-role " +
                    "allowlist; host/Docker/SSH/Git/shell/credential variables are never settable.");
            if (value.Length > SandboxPolicy.MaxEnvironmentValueLength || value.Any(char.IsControl))
                throw new InvalidOperationException(
                    $"create request environment '{key}' has an unsafe or overlong value.");
            environment.Add(key, value);
        }

        return new DockerCreateRequest(
            containerName, exactImageId, identity, networkName,
            evidence.Cpus, evidence.MemoryMb, evidence.Pids,
            labels, environment, revalidatedWorkspaceSource);
    }

    /// <summary>
    /// EXACT (role, policy) tuple mapping to the concrete validated network argument. Every
    /// other combination throws: the role and the policy must agree. Coder is limited to the
    /// validated model network; Reviewer and Tester are exactly offline (`none`); Restore is
    /// limited to the validated configured restore network. Host, default bridge, arbitrary
    /// names and caller-supplied raw network flags are unreachable.
    /// </summary>
    internal static string ResolveNetworkName(
        SandboxRole role, SandboxNetworkPolicy policy, SandboxConfig config)
    {
        return (role, policy) switch
        {
            (SandboxRole.Coder, SandboxNetworkPolicy.Model) => RequireValidNetwork(
                config.ModelNetwork, "sandbox.model_network"),
            (SandboxRole.Reviewer, SandboxNetworkPolicy.None) => "none",
            (SandboxRole.Tester, SandboxNetworkPolicy.None) => "none",
            (SandboxRole.Restore, SandboxNetworkPolicy.Restore) => RequireValidNetwork(
                config.Roles.Tester.Restore.NetworkName, "sandbox.roles.tester.restore.network_name"),
            _ => throw new InvalidOperationException(
                $"the sandbox network policy {policy} does not match the role {role}: the " +
                "closed role/network mapping is enforced on the exact validated tuple."),
        };
    }

    private static string RequireValidNetwork(string? name, string field)
    {
        if (string.IsNullOrWhiteSpace(name) || !SandboxConfig.IsValidDockerNetworkName(name))
            throw new InvalidOperationException(
                $"{field} '{StrictJson.Bounded(name ?? "")}' is not a permitted Docker network " +
                "name: reserved networks (host, bridge, none, default), malformed names and " +
                "whitespace/control characters are rejected. Host networking is never permitted.");
        return name!;
    }

    internal static FrozenDictionary<string, string> ValidateAndSnapshotLabels(
        IReadOnlyDictionary<string, string> labels, string what)
    {
        if (labels is null || labels.Count == 0)
            throw new InvalidOperationException(
                $"{what} require the complete Tenninety management identity; empty label sets are rejected.");
        foreach (var key in SandboxSpec.RequiredLabelKeys)
        {
            if (!labels.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"{what} require the management label '{key}' with a non-blank value; " +
                    "partial label sets are rejected.");
        }
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in labels)
        {
            if (!SandboxPolicy.PermittedLabelKeys.Contains(key))
                throw new InvalidOperationException(
                    $"label '{key}' is not a permitted Tenninety management identity key; " +
                    "unknown labels are rejected.");
            if (value.Length > SandboxPolicy.MaxLabelValueLength || value.Any(char.IsControl))
                throw new InvalidOperationException(
                    $"label '{key}' has an unsafe or overlong value.");
            snapshot.Add(key, value);
        }
        return snapshot.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Role-correct network enforcement: the resolved name is re-checked against the
    /// closed mapping independent of any config object. Coder and Restore must use a validated
    /// named pre-existing network; Reviewer and Tester exactly `none`. Host, default bridge,
    /// arbitrary names and caller-supplied raw network flags are unreachable.</summary>
    private static void RequireNetworkArg(SandboxRole role, string networkName)
    {
        switch (role)
        {
            case SandboxRole.Reviewer or SandboxRole.Tester:
                if (networkName != "none")
                    throw new InvalidOperationException(
                        $"the {role.ToString().ToLowerInvariant()} sandbox network must be exactly " +
                        $"'none' (offline) but is '{StrictJson.Bounded(networkName)}'.");
                return;
            case SandboxRole.Coder or SandboxRole.Restore:
                if (networkName == "none" ||
                    !Tenninety.Core.Models.SandboxConfig.IsValidDockerNetworkName(networkName))
                    throw new InvalidOperationException(
                        $"the {role.ToString().ToLowerInvariant()} sandbox network must be the " +
                        "validated pre-existing named network; 'none', reserved networks " +
                        "(host, bridge, none, default) and malformed names are rejected. " +
                        "Host networking is never permitted.");
                return;
            default:
                throw new InvalidOperationException($"unknown sandbox role {role}.");
        }
    }
}

/// <summary>
/// Typed exec request for one command inside a running sandbox container. INTERNAL and
/// constructible ONLY through <see cref="FromCommand"/>, which revalidates EVERY command
/// invariant (executable, arguments, stdin bytes, timeout, output cap, working directory,
/// command environment) before snapshotting — a validated <see cref="SandboxCommand"/> alone
/// is never trusted as sufficient.
/// </summary>
internal sealed class DockerExecRequest
{
    public string ContainerId { get; }
    public string Executable { get; }
    public IReadOnlyList<string> Arguments { get; }
    /// <summary>Validated in-container working directory: /workspace or strictly beneath it.</summary>
    public string WorkingDirectory { get; }
    public string? StdIn { get; }
    public TimeSpan Timeout { get; }
    public long MaxOutputBytes { get; }
    /// <summary>Sorted command environment (closed allowlist keys only; immutable snapshot).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; }

    private DockerExecRequest(
        string containerId, string executable, IReadOnlyList<string> arguments,
        string workingDirectory, string? stdIn, TimeSpan timeout, long maxOutputBytes,
        IReadOnlyDictionary<string, string> environment)
    {
        ContainerId = containerId;
        Executable = executable;
        Arguments = arguments;
        WorkingDirectory = workingDirectory;
        StdIn = stdIn;
        Timeout = timeout;
        MaxOutputBytes = maxOutputBytes;
        Environment = environment;
    }

    internal static DockerExecRequest FromCommand(
        string containerId, SandboxCommand command, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(command);
        DockerValidation.RequireContainerId(containerId, "exec request container id");

        // Revalidate every command invariant here — construction never relies on the
        // command having validated itself earlier.
        if (string.IsNullOrEmpty(command.Executable) || command.Executable.Contains('\0'))
            throw new InvalidOperationException("exec request needs a concrete executable.");
        var arguments = new List<string>(command.Arguments);
        if (arguments.Any(a => a is null || a.Contains('\0')))
            throw new InvalidOperationException("exec request arguments contain null or NUL bytes.");
        if (!SandboxCommand.IsSafeGuestWorkingDirectory(command.WorkingDirectory))
            throw new InvalidOperationException(
                "exec request working directory must be exactly /workspace or strictly beneath it.");
        if (timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("exec request timeout must be positive.");
        if (command.MaxOutputBytes <= 0 || command.MaxOutputBytes > 64L * 1024 * 1024)
            throw new InvalidOperationException(
                $"exec request output cap {command.MaxOutputBytes} is outside (0, 64 MiB].");
        string? stdIn = command.StdIn;
        if (stdIn is not null)
        {
            if (stdIn.Contains('\0'))
                throw new InvalidOperationException("exec request stdin contains NUL bytes.");
            if (System.Text.Encoding.UTF8.GetByteCount(stdIn) > DockerCliInvocation.MaxStdInBytes)
                throw new InvalidOperationException(
                    $"exec request stdin exceeds the {DockerCliInvocation.MaxStdInBytes}-byte UTF-8 cap.");
        }
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in command.Environment)
        {
            if (!SandboxPolicy.PermittedCommandEnvironmentKeys.Contains(key))
                throw new InvalidOperationException(
                    $"exec request env key '{key}' is not on the closed command allowlist; " +
                    "host/Docker/SSH/Git/shell/credential variables are never settable.");
            if (value.Length > SandboxPolicy.MaxEnvironmentValueLength || value.Any(char.IsControl))
                throw new InvalidOperationException(
                    $"exec request env '{key}' has an unsafe or overlong value.");
            environment.Add(key, value);
        }

        return new DockerExecRequest(
            containerId, command.Executable, arguments.AsReadOnly(),
            command.WorkingDirectory, stdIn, timeout, command.MaxOutputBytes,
            environment.ToFrozenDictionary(StringComparer.Ordinal));
    }
}

/// <summary>
/// Complete scoped Tenninety management identity for labelled container listing. INTERNAL and
/// constructible only through <see cref="FromManagementIdentity"/>; the validated labels are
/// snapshotted so mutating the caller's dictionary afterwards cannot change the scope, and an
/// empty, partial, unknown or unsafe label set is rejected: scoped cleanup can never degrade
/// into an unscoped list-and-delete primitive.
/// </summary>
internal sealed class DockerContainerScope
{
    /// <summary>Immutable snapshot of the validated management identity.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; }

    private DockerContainerScope(IReadOnlyDictionary<string, string> labels) => Labels = labels;

    internal static DockerContainerScope FromManagementIdentity(
        IReadOnlyDictionary<string, string> labels)
    {
        var snapshot = DockerCreateRequest.ValidateAndSnapshotLabels(
            labels, "a labelled container list");
        return new DockerContainerScope(snapshot);
    }
}

/// <summary>Repository-wide recovery scope. Exactly the instance and non-secret repository
/// identity labels are accepted; this permits startup inventory without an unscoped Docker
/// list while the daemon lock prevents a second live Tenninety process for the repository.</summary>
internal sealed class DockerRecoveryScope
{
    public IReadOnlyDictionary<string, string> Labels { get; }

    private DockerRecoveryScope(IReadOnlyDictionary<string, string> labels) => Labels = labels;

    internal static DockerRecoveryScope Create(string instance, string repository)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenninety.instance"] = instance,
            ["tenninety.repository"] = repository,
        };
        foreach (var (key, value) in labels)
        {
            if (!SandboxPolicy.PermittedLabelKeys.Contains(key) ||
                string.IsNullOrWhiteSpace(value) ||
                !SandboxPolicy.IsSafePolicyValue(value, SandboxPolicy.MaxLabelValueLength))
                throw new InvalidOperationException(
                    "sandbox recovery requires bounded instance and repository identities.");
        }
        if (repository.StartsWith('/') || repository.Contains('\\') || repository.Contains(':'))
            throw new InvalidOperationException(
                "sandbox recovery repository identity must not be a raw host path.");
        return new DockerRecoveryScope(
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(labels));
    }
}
