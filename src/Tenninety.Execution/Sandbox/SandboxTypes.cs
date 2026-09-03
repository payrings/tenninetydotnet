using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

using Tenninety.Core.Models;

namespace Tenninety.Execution.Sandbox;

/// <summary>Role of one disposable sandbox invocation. Restore is the separate, bounded
/// restricted-network phase that may run over the tester attempt workspace.</summary>
public enum SandboxRole
{
    Coder,
    Reviewer,
    Tester,
    Restore,
}

/// <summary>
/// Closed network policy for a sandbox container. There is deliberately no value that carries
/// an arbitrary Docker network name: trusted Docker infrastructure maps these policies to the
/// validated configured network names (or to no network at all) when it builds the container.
/// </summary>
public enum SandboxNetworkPolicy
{
    /// <summary>No network attachment at all (reviewer, tester).</summary>
    None,

    /// <summary>Attach only to the pre-existing validated model network (coder).</summary>
    Model,

    /// <summary>Attach only to the pre-existing validated restricted restore network
    /// with its required proxy boundary (restore phase).</summary>
    Restore,
}

/// <summary>Defensive snapshot helpers: security-sensitive collections are frozen at
/// construction so a caller can never mutate the original input after validation and thereby
/// change what the runtime executes.</summary>
internal static class FrozenCollections
{
    public static readonly ReadOnlyDictionary<string, string> EmptyDictionary =
        new(new Dictionary<string, string>());

    public static readonly ReadOnlyCollection<string> EmptyArguments = new([]);

    public static ReadOnlyDictionary<string, string> Dictionary(
        IReadOnlyDictionary<string, string>? source) =>
        source is null
            ? EmptyDictionary
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(source, StringComparer.Ordinal));

    public static ReadOnlyCollection<string> Arguments(IReadOnlyList<string>? source) =>
        source is null ? EmptyArguments : new List<string>(source).AsReadOnly();
}

/// <summary>
/// Typed, non-extensible specification of one sandbox container. There is intentionally no
/// property that can carry raw Docker arguments, mounts, devices, published ports, capabilities
/// or arbitrary networks: the runtime derives every hardening flag from this spec plus the
/// fixed <see cref="SandboxPolicy"/>.
///
/// The spec enforces its own invariants: the image must be digest-pinned (never a mutable tag),
/// the host workspace must be a <see cref="ValidatedSandboxWorkspacePath"/> (never a raw
/// unchecked string), the network is a closed policy enum, and labels/environment are frozen
/// snapshots validated against closed allowlists.
///
/// This type is a trusted control-plane input for <see cref="ISandboxRuntime"/> implementations;
/// it is never handed to agents. The validated host scratch path stays here (excluded from
/// serialization) and is never exposed through the agent/session-facing
/// <see cref="SandboxSessionInfo"/>.
/// </summary>
public sealed partial class SandboxSpec
{
    public required SandboxRole Role { get; init; }

    /// <summary>Digest-pinned image reference or exact local image ID. Mutable tags are
    /// rejected by <see cref="Validate"/> so an attempt always runs the image it verified.</summary>
    public required string Image { get; init; }

    /// <summary>Fresh, validated, disposable host directory mounted at /workspace. Trusted
    /// orchestration/transport data only: never serialized, never the authoritative repository,
    /// never copied into generic agent context. Excluded from serialization;
    /// <see cref="Validate"/> fails closed when it is missing.</summary>
    [JsonIgnore]
    public ValidatedSandboxWorkspacePath? HostWorkspacePath { get; init; }

    /// <summary>Closed network policy; never an arbitrary network name.</summary>
    public required SandboxNetworkPolicy Network { get; init; }

    public required double Cpus { get; init; }
    public required int MemoryMb { get; init; }
    public required int Pids { get; init; }
    public required TimeSpan Timeout { get; init; }

    private IReadOnlyDictionary<string, string>? _labels;

    /// <summary>Tenninety management labels (instance/repository/run/WP/attempt/role identity).
    /// Keys are restricted to the closed Tenninety label policy; the stored collection is a
    /// defensive snapshot frozen at construction.</summary>
    public IReadOnlyDictionary<string, string> Labels
    {
        get => _labels ?? FrozenCollections.EmptyDictionary;
        init => _labels = FrozenCollections.Dictionary(value);
    }

    private IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Minimal role-specific environment from the closed per-role allowlist. Credential
    /// stores, host HOME, SSH/git/agent configuration are never representable here. The stored
    /// collection is a defensive snapshot frozen at construction.</summary>
    public IReadOnlyDictionary<string, string> Environment
    {
        get => _environment ?? FrozenCollections.EmptyDictionary;
        init => _environment = FrozenCollections.Dictionary(value);
    }

    /// <summary>Exact candidate commit the workspace was materialized from, when known.</summary>
    public string? CandidateSha { get; init; }

    /// <summary>The management identity labels every sandbox spec MUST carry (values validated
    /// below). `tenninety.candidate` is required exactly when <see cref="CandidateSha"/> is set.</summary>
    public static IReadOnlyList<string> RequiredLabelKeys { get; } =
    [
        "tenninety.instance",
        "tenninety.repository",
        "tenninety.run",
        "tenninety.wp",
        "tenninety.attempt",
        "tenninety.role",
    ];

    // ---- Fixed by policy: not settable anywhere in the type system. ----

    public string ContainerWorkspacePath => SandboxPolicy.ContainerWorkspacePath;

    public bool ReadOnlyRootFileSystem => SandboxPolicy.ReadOnlyRootFileSystem;

    public IReadOnlyList<TmpfsMount> TmpfsMounts => SandboxPolicy.FixedTmpfsMounts;

    /// <summary>Fails closed on any value the runtime must never receive.</summary>
    public void Validate() => ValidateAndCapture();

    /// <summary>
    /// Validates the spec and returns an internal, immutable evidence token carrying the
    /// validated frozen values. Trusted factories (<see cref="DockerCreateRequest.FromSpec"/>)
    /// REQUIRE this token, so a create request can never be built from values that were not
    /// proven by a full <see cref="Validate"/> run — evidence cannot be manufactured outside
    /// this assembly.
    /// </summary>
    internal SandboxSpecEvidence ValidateAndCapture()
    {
        if (!SandboxConfig.IsPinnedImageReference(Image))
            throw new InvalidOperationException(
                $"sandbox spec for {Role} has an unpinned image '{Image}': only a registry " +
                "reference with @sha256:<64 lowercase hex> or an exact sha256:<64 lowercase hex> " +
                "local image ID is acceptable, so an attempt always runs the image it validated.");
        if (HostWorkspacePath is null)
            throw new InvalidOperationException(
                $"sandbox spec for {Role} needs a validated disposable host workspace path " +
                "(ValidatedSandboxWorkspacePath.Create); a raw unchecked string is never accepted.");
        var expectedNetwork = Role switch
        {
            SandboxRole.Coder => SandboxNetworkPolicy.Model,
            SandboxRole.Reviewer or SandboxRole.Tester => SandboxNetworkPolicy.None,
            SandboxRole.Restore => SandboxNetworkPolicy.Restore,
            _ => throw new InvalidOperationException(
                $"unknown sandbox role {Role}."),
        };
        if (Network != expectedNetwork)
            throw new InvalidOperationException(
                $"sandbox spec for {Role} requires network policy {expectedNetwork}, " +
                $"got {Network}.");
        if (Cpus is double.NaN or double.PositiveInfinity or <= 0 or > 256)
            throw new InvalidOperationException($"sandbox spec for {Role} has invalid Cpus {Cpus}.");
        if (MemoryMb is < 128 or > 1_048_576)
            throw new InvalidOperationException(
                $"sandbox spec for {Role} has invalid MemoryMb {MemoryMb}.");
        if (Pids is < 1 or > 32_768)
            throw new InvalidOperationException($"sandbox spec for {Role} has invalid Pids {Pids}.");
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(24))
            throw new InvalidOperationException(
                $"sandbox spec for {Role} has invalid Timeout {Timeout}.");

        foreach (var (key, value) in Labels)
        {
            if (!SandboxPolicy.PermittedLabelKeys.Contains(key))
                throw new InvalidOperationException(
                    $"sandbox spec for {Role} label '{key}' is not a permitted Tenninety " +
                    "management identity key.");
            if (!SandboxPolicy.IsSafePolicyValue(value, SandboxPolicy.MaxLabelValueLength))
                throw new InvalidOperationException(
                    $"sandbox spec for {Role} label '{key}' has an unsafe or overlong value.");
        }
        var permittedEnvironment = SandboxPolicy.PermittedEnvironmentKeys(Role);
        foreach (var (key, value) in Environment)
        {
            if (!permittedEnvironment.Contains(key))
                throw new InvalidOperationException(
                    $"sandbox spec for {Role} environment key '{key}' is not on the closed " +
                    "per-role allowlist; host/Docker/SSH/Git/shell/credential variables are " +
                    "never settable through a sandbox spec.");
            if (!SandboxPolicy.IsSafePolicyValue(value, SandboxPolicy.MaxEnvironmentValueLength))
                throw new InvalidOperationException(
                    $"sandbox spec for {Role} environment '{key}' has an unsafe or overlong value.");
        }

        // ---- complete management identity is mandatory ------------------------------
        foreach (var key in RequiredLabelKeys)
        {
            if (!Labels.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException(
                    $"sandbox spec for {Role} requires the management label '{key}' with a " +
                    "non-blank, control-character-free value.");
        }
        var roleName = Role.ToString().ToLowerInvariant();
        if (!string.Equals(Labels["tenninety.role"], roleName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"sandbox spec label 'tenninety.role' must equal the normalized role " +
                $"'{roleName}' but is '{Labels["tenninety.role"]}'.");
        var repositoryIdentity = Labels["tenninety.repository"];
        if (repositoryIdentity.StartsWith('/') || repositoryIdentity.Contains('\\') ||
            repositoryIdentity.Contains(':'))
            throw new InvalidOperationException(
                "sandbox spec label 'tenninety.repository' must be a non-secret repository " +
                "identity (a name/slug), never a raw absolute host path.");
        var hasCandidateLabel = Labels.TryGetValue("tenninety.candidate", out var candidateLabel);
        if (CandidateSha is { } sha)
        {
            if (!hasCandidateLabel || !string.Equals(candidateLabel, sha, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "sandbox spec label 'tenninety.candidate' must be present and exactly " +
                    "match CandidateSha whenever a candidate is set.");
        }
        else if (hasCandidateLabel)
        {
            throw new InvalidOperationException(
                "sandbox spec label 'tenninety.candidate' is set without a CandidateSha: " +
                "the candidate identity is inconsistent.");
        }

        // The labels/environment surfaces are already defensive frozen snapshots; the
        // evidence carries those snapshots (and the validated network policy) so factories
        // read only validated values.
        return new SandboxSpecEvidence(
            Role, Image, Network, Cpus, MemoryMb, Pids, Labels, Environment);
    }
}

/// <summary>
/// Immutable, internal proof that one <see cref="SandboxSpec"/> passed its complete
/// validation. Trusted request factories require this token; it cannot be constructed
/// outside this assembly, so validated values can never be bypassed or manufactured.
/// </summary>
internal sealed record SandboxSpecEvidence(
    SandboxRole Role,
    string ImageReference,
    SandboxNetworkPolicy Network,
    double Cpus,
    int MemoryMb,
    int Pids,
    IReadOnlyDictionary<string, string> Labels,
    IReadOnlyDictionary<string, string> Environment);

/// <summary>
/// One command to execute inside a running sandbox. Host-side invocation is always
/// structured (ordered argument list); no shell string is ever assembled from this data.
/// Arguments are opaque byte strings: spaces and shell metacharacters inside one argument stay
/// inside that one argument and are never re-parsed by any host shell.
/// </summary>
public sealed partial class SandboxCommand
{
    /// <summary>Executable resolved inside the container.</summary>
    public required string Executable { get; init; }

    private IReadOnlyList<string>? _arguments;

    /// <summary>Ordered argument list, passed verbatim (no re-quoting, no shell). Stored as a
    /// defensive snapshot frozen at construction.</summary>
    public IReadOnlyList<string> Arguments
    {
        get => _arguments ?? FrozenCollections.EmptyArguments;
        init => _arguments = FrozenCollections.Arguments(value);
    }

    /// <summary>Working directory inside the container; defaults to the workspace mount.
    /// Must be exactly /workspace or strictly beneath it.</summary>
    public string WorkingDirectory { get; init; } = SandboxPolicy.ContainerWorkspacePath;

    /// <summary>Optional bounded stdin payload.</summary>
    public string? StdIn { get; init; }

    /// <summary>Per-command timeout; the runtime enforces that it never exceeds the session timeout.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Combined output cap in bytes; exceeding it terminates the whole session and
    /// marks the result output_truncated.</summary>
    public long MaxOutputBytes { get; init; } = 1_048_576;

    private IReadOnlyDictionary<string, string>? _environment;

    /// <summary>Additional environment restricted to the exact closed command allowlist.
    /// Stored as a defensive snapshot frozen at construction.</summary>
    public IReadOnlyDictionary<string, string> Environment
    {
        get => _environment ?? FrozenCollections.EmptyDictionary;
        init => _environment = FrozenCollections.Dictionary(value);
    }

    public void Validate(TimeSpan sessionTimeout)
    {
        if (Executable.Length == 0 || Executable.Contains('\0'))
            throw new InvalidOperationException("sandbox command needs a concrete executable.");
        if (Arguments.Any(a => a is null || a.Contains('\0')))
            throw new InvalidOperationException("sandbox command arguments contain a null element or a NUL byte.");
        if (!IsSafeGuestWorkingDirectory(WorkingDirectory))
            throw new InvalidOperationException(
                $"sandbox commands must run exactly at {SandboxPolicy.ContainerWorkspacePath} " +
                $"or strictly beneath it, got '{WorkingDirectory}'.");
        if (Timeout is { } t && (t <= TimeSpan.Zero || t > sessionTimeout))
            throw new InvalidOperationException(
                "per-command timeout must be positive and never exceed the session timeout.");
        if (MaxOutputBytes <= 0 || MaxOutputBytes > 64L * 1024 * 1024)
            throw new InvalidOperationException(
                $"sandbox command output cap {MaxOutputBytes} is outside (0, 64 MiB].");
        if (StdIn is { } stdIn && stdIn.Length > 1_048_576)
            throw new InvalidOperationException("sandbox command stdin exceeds 1 MiB.");
        foreach (var (key, value) in Environment)
        {
            if (!SandboxPolicy.PermittedCommandEnvironmentKeys.Contains(key))
                throw new InvalidOperationException(
                    $"sandbox command env key '{key}' is not on the closed command allowlist; " +
                    "host/Docker/SSH/Git/shell/credential variables are never settable.");
            if (!SandboxPolicy.IsSafePolicyValue(value, SandboxPolicy.MaxEnvironmentValueLength))
                throw new InvalidOperationException(
                    $"sandbox command env '{key}' has an unsafe or overlong value.");
        }
    }

    /// <summary>
    /// Deterministic POSIX check for a guest working directory: absolute, NUL-free, exactly
    /// <see cref="SandboxPolicy.ContainerWorkspacePath"/> or strictly beneath it with an exact
    /// segment boundary, no empty/'.'/'..' segments and no backslashes. Deliberately avoids
    /// host <see cref="Path"/> APIs so Windows path semantics can never influence the verdict.
    /// </summary>
    public static bool IsSafeGuestWorkingDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0') || path.Contains('\\')) return false;
        if (!path.StartsWith('/')) return false;
        if (!path.StartsWith(SandboxPolicy.ContainerWorkspacePath, StringComparison.Ordinal))
            return false;
        // Exact boundary: "/workspace" itself, or "/workspace/<segments...>".
        if (path.Length != SandboxPolicy.ContainerWorkspacePath.Length &&
            !path.StartsWith(SandboxPolicy.ContainerWorkspacePath + "/", StringComparison.Ordinal))
            return false;
        var segments = path.Split('/');
        // Leading "/" yields one empty first segment; any other empty segment means "//" or a
        // trailing "/", both rejected so normalization can never reinterpret the path.
        return segments.Skip(1).All(s => s.Length > 0 && s != "." && s != "..");
    }
}

/// <summary>Bounded result of one sandbox command execution.
///
/// Capture contract (do not shorten it downstream): <see cref="StdOutTail"/> and
/// <see cref="StdErrTail"/> carry the COMPLETE captured output as already bounded by the
/// transport's combined capture cap — the names are historical; these are NOT a presentation
/// tail. Zero-test detection and every other decision must see this complete bounded output;
/// presentation shortening (the final report bound) happens only AFTER classification and
/// sanitization, in <see cref="Testing.TestOutputClassifier"/>.
///
/// Exit-code semantics: a definitive command exit is always >= 0. A negative exit code is
/// synthetic: it is produced by the adapter/transport for an operational outcome that carries
/// an explicit flag (<see cref="TimedOut"/>, <see cref="Cancelled"/>, <see cref="OomKilled"/>,
/// <see cref="OutputTruncated"/>) — or, with NO flag set, for a process that never produced a
/// definitive exit at all (startup or transport I/O failure). See
/// <see cref="SyntheticInfrastructureFailure"/>.</summary>
public sealed record SandboxCommandResult(
    int ExitCode,
    string StdOutTail,
    string StdErrTail,
    bool TimedOut,
    bool Cancelled,
    bool OomKilled,
    bool OutputTruncated,
    TimeSpan Duration)
{
    /// <summary>Success requires a zero exit AND no timeout, cancellation, OOM kill or output
    /// truncation: a truncated result proves the command's observable behaviour was cut short,
    /// so it can never be treated as a pass.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled && !OomKilled && !OutputTruncated;

    /// <summary>
    /// True when the result carries a synthetic negative exit WITHOUT any operational flag —
    /// the typed signature of a command whose process could not be started or whose transport
    /// failed during I/O. The candidate command produced NO definitive exit, so this is an
    /// infrastructure failure: it is never an ordinary candidate failure and never a verdict.
    /// Flagged operational outcomes (timeout, cancellation, OOM, truncation) keep their own
    /// documented classification and are deliberately NOT included.</summary>
    public bool SyntheticInfrastructureFailure =>
        ExitCode < 0 && !TimedOut && !Cancelled && !OomKilled && !OutputTruncated;
}

/// <summary>
/// Sanitized, agent/session-facing description of a sandbox session. It carries only a runtime
/// session ID, the role, the lifecycle state and the fixed container workspace path. It cannot
/// represent a host path (in particular neither the disposable scratch path nor the
/// authoritative repository), so it is safe to hand to any agent-facing context.
/// </summary>
public sealed record SandboxSessionInfo(
    string ContainerId,
    SandboxRole Role,
    SandboxSessionState State)
{
    public string ContainerWorkspacePath => SandboxPolicy.ContainerWorkspacePath;
}

/// <summary>Lifecycle state of a sandbox session. The quiescent states are the only ones from
/// which trusted extraction may ever consider the container's filesystem final.</summary>
public enum SandboxSessionState
{
    Created,
    Running,
    Stopping,
    /// <summary>Container confirmed not running: writes are quiescent.</summary>
    StoppedQuiescent,
    /// <summary>Stop could not be confirmed; extraction is forbidden and the workspace is quarantined.</summary>
    StoppedUnconfirmed,
    Failed,
    Disposed,
}
