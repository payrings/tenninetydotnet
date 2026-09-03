using Tenninety.Core.Models;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Narrow typed adapter over <see cref="IDockerCliTransport"/>. Owns Docker CLI syntax and
/// argument ordering; callers pass validated typed requests and never raw Docker argument
/// vectors, extra flags, or shell strings. Every operation asserts the full deterministic
/// argument vector shape in tests, validates identifiers before reuse, parses structured
/// output strictly (duplicate fields rejected), classifies absence versus operational
/// failure, and bounds/sanitizes every failure message.
/// </summary>
public sealed class DockerCli
{
    private readonly IDockerCliTransport _transport;
    private readonly Func<string, string?> _environmentLookup;

    public DockerCli(IDockerCliTransport transport)
        : this(transport, Environment.GetEnvironmentVariable)
    {
    }

    /// <summary>Seam constructor (InternalsVisibleTo): deterministic host-environment lookup
    /// so tests can assert DOCKER_HOST scrubbing without mutating process-wide state.</summary>
    internal DockerCli(IDockerCliTransport transport, Func<string, string?> environmentLookup)
    {
        _transport = transport;
        _environmentLookup = environmentLookup;
    }

    // ---- daemon connectivity ------------------------------------------------

    /// <summary>`docker version --format {{json .}}` — proves client+daemon connectivity;
    /// parses the real {"Client":{…},"Server":{…}} shape and fails closed without a Server.</summary>
    public async Task<DockerDaemonInfo> GetVersionAsync(CancellationToken ct = default)
    {
        var inv = new DockerCliInvocation(["version", "--format", "{{json .}}"]);
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "version");
        return DockerJsonParsing.ParseVersionOutput(
            System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
    }

    /// <summary>`docker info --format {{json .}}` — structured daemon facts.</summary>
    public async Task<DockerDaemonInfo> GetInfoAsync(CancellationToken ct = default)
    {
        var inv = new DockerCliInvocation(["info", "--format", "{{json .}}"]);
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "info");
        return DockerJsonParsing.ParseFullDaemonInfo(
            System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
    }

    /// <summary>Single `docker info` call returning the daemon facts plus a hostile-output
    /// marker for SecurityOptions (a non-array shape must never count as LSM evidence).</summary>
    public async Task<DockerDaemonFacts> GetDaemonInfoAsync(CancellationToken ct = default)
    {
        var inv = new DockerCliInvocation(["info", "--format", "{{json .}}"]);
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "info");
        var json = System.Text.Encoding.UTF8.GetBytes(result.StandardOutput);
        return new DockerDaemonFacts(
            DockerJsonParsing.ParseFullDaemonInfo(json),
            DockerJsonParsing.HasMalformedSecurityOptions(json));
    }

    // ---- image inspect with digest evidence verification ----------------------

    /// <summary>
    /// Inspects a pinned image reference locally (never pulls) and verifies evidence:
    ///  - an exact `sha256:<64 hex>` reference must resolve to exactly that local ID;
    ///  - a `repository@sha256:<digest>` reference must be backed by matching
    ///    repository-digest evidence in RepoDigests: no evidence, evidence for a different
    ///    repository, digest mismatch, or contradictory digest entries all fail closed.
    /// </summary>
    public async Task<DockerImageInfo> InspectImageAsync(string pinnedReference, CancellationToken ct = default)
    {
        if (SandboxConfig.IsPinnedImageReference(pinnedReference) == false || pinnedReference.Contains(' '))
            throw new InvalidOperationException(
                "image inspect requires a digest-pinned reference (registry@sha256:<64 hex> or " +
                $"exact sha256:<64 hex>), got '{StrictJson.Bounded(pinnedReference)}'.");
        var inv = new DockerCliInvocation(["image", "inspect", pinnedReference]);
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "image inspect", scrub: null);
        var info = DockerJsonParsing.ParseImageInspect(
            System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
        VerifyPinnedReference(pinnedReference, info);
        return info;
    }

    private static void VerifyPinnedReference(string pinnedReference, DockerImageInfo info)
    {
        if (pinnedReference.StartsWith("sha256:", StringComparison.Ordinal))
        {
            if (!string.Equals(info.ImageId, pinnedReference, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"the exact local image id '{StrictJson.Bounded(info.ImageId)}' does not match " +
                    "the requested pinned image id; the requested image is not available locally " +
                    "under that exact identity. The image is never pulled.");
            return;
        }

        // repository@sha256:<digest>: require matching repository-digest evidence.
        var at = pinnedReference.LastIndexOf('@');
        var requestedRepo = pinnedReference[..at];
        var requestedDigest = pinnedReference[(at + 1)..]; // sha256:<hex>

        static string RepoOf(string repoDigest)
        {
            var idx = repoDigest.LastIndexOf('@');
            return idx <= 0 ? "" : repoDigest[..idx];
        }

        var repoEntries = info.RepoDigests
            .Where(rd => RepoOf(rd) == requestedRepo)
            .ToList();
        if (repoEntries.Count == 0)
            throw new InvalidOperationException(
                $"no repository-digest evidence for '{StrictJson.Bounded(requestedRepo)}' in the " +
                $"inspected image (digests: {info.RepoDigests.Count} entries). A pinned registry " +
                "reference must be locally backed by matching evidence; the image is never pulled.");
        var digests = repoEntries
            .Select(rd => rd[(rd.LastIndexOf('@') + 1)..])
            .ToHashSet(StringComparer.Ordinal);
        if (digests.Count > 1)
            throw new InvalidOperationException(
                "contradictory repository-digest evidence for the requested repository: " +
                "multiple distinct digests are present; refusing ambiguous identity.");
        if (!digests.Contains(requestedDigest))
            throw new InvalidOperationException(
                "the locally inspected repository digest does not match the pinned digest " +
                "for the requested repository; refusing to substitute a different image.");
    }

    // ---- network inspect ------------------------------------------------------

    /// <summary>
    /// Inspects a network. Returns null ONLY when absence is positively established
    /// (non-timeout exit failure with "No such network"). Timeout, cancellation, truncation,
    /// malformed output and other operational failures throw structured errors instead of
    /// being silently converted to "network absent". The parsed Name must equal the
    /// requested network.
    /// </summary>
    public async Task<DockerNetworkInfo?> InspectNetworkAsync(string networkName, CancellationToken ct = default)
    {
        // The same closed Docker network-name validation used everywhere else in the
        // runtime: reserved networks (host, bridge, none, default), malformed names, and
        // names with whitespace or control characters can never be inspected.
        if (!Tenninety.Core.Models.SandboxConfig.IsValidDockerNetworkName(networkName))
            throw new InvalidOperationException(
                $"network inspect requires a permitted Docker network name; " +
                $"'{StrictJson.Bounded(networkName)}' is malformed or a reserved network " +
                "(host, bridge, none, default).");
        var inv = new DockerCliInvocation(["network", "inspect", networkName]);
        var result = await _transport.RunAsync(inv, ct);
        if (!result.Succeeded)
        {
            if (IndicatesAbsent(result, "No such network") ||
                IndicatesNetworkNotFound(result))
                return null;
            throw new InvalidOperationException(
                $"docker network inspect failed ({DescribeFailure(result)}).");
        }
        var info = DockerNetworkInfo.FromJson(
            System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
        if (!string.Equals(info.Name, networkName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"docker network inspect returned network '{StrictJson.Bounded(info.Name)}' " +
                $"but '{StrictJson.Bounded(networkName)}' was requested; refusing mismatched identity.");
        return info;
    }

    // ---- container lifecycle ----------------------------------------------------

    /// <summary>Typed `docker create` from one validated request. Returns the validated full
    /// container ID. Failure messages are bounded and the workspace source is scrubbed so a
    /// Docker create error cannot leak a host path.</summary>
    internal async Task<string> CreateContainerAsync(DockerCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DockerValidation.RequireImageId(request.ExactImageId, "create request image id");
        DockerValidation.RequireNetworkArg(request.NetworkName, "create request network");
        DockerValidation.RequireMountSource(request.WorkspaceSource, "create request workspace source");
        DockerValidation.RequireContainerName(request.ContainerName, "create request container name");
        if (request.User.IsRoot)
            throw new InvalidOperationException("create request identity is root (uid=0); refusing.");
        if (request.Cpus is double.NaN or double.PositiveInfinity or <= 0 or > 256)
            throw new InvalidOperationException("create request has invalid Cpus.");
        if (request.MemoryMb is < 128 or > 1_048_576)
            throw new InvalidOperationException("create request has invalid MemoryMb.");
        if (request.Pids is < 1 or > 32_768)
            throw new InvalidOperationException("create request has invalid Pids.");
        if (request.WaitingCommand is not { Count: > 0 } ||
            request.WaitingCommand.Any(a => string.IsNullOrWhiteSpace(a) || a.Contains('\0')))
            throw new InvalidOperationException("create request needs a concrete fixed waiting command.");

        var inv = new DockerCliInvocation(BuildCreateArguments(request), timeout: TimeSpan.FromMinutes(2));
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "container create", scrub: request.WorkspaceSource);
        var containerId = result.StandardOutput.Trim();
        DockerValidation.RequireContainerId(containerId, "docker create output");
        return containerId;
    }

    /// <summary>Deterministic create argument vector (asserted in full by tests).</summary>
    internal static IReadOnlyList<string> BuildCreateArguments(DockerCreateRequest request)
    {
        var args = new List<string>
        {
            "create",
            "--name", request.ContainerName,
            "--pull=never",
            "--read-only",
            "--cap-drop=ALL",
            "--security-opt=no-new-privileges",
            "--workdir", request.ContainerWorkspaceTarget,
            "--cpus", FormatCpus(request.Cpus),
            "--memory", $"{request.MemoryMb}m",
            "--pids-limit", request.Pids.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--ulimit", DockerCreateRequest.NoFileUlimit,
            "--network", request.NetworkName,
            "--user", request.User.ToUserFlag(),
        };
        foreach (var tmpfs in request.Tmpfs)
        {
            args.Add("--tmpfs");
            args.Add($"{tmpfs.ContainerPath}:{tmpfs.Options}");
        }
        foreach (var kv in request.Labels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            args.Add("--label");
            args.Add($"{kv.Key}={kv.Value}");
        }
        foreach (var kv in request.Environment.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            args.Add("--env");
            args.Add($"{kv.Key}={kv.Value}");
        }
        args.Add("--mount");
        args.Add($"type=bind,source={request.WorkspaceSource},target={request.ContainerWorkspaceTarget},bind-propagation=rprivate");
        args.Add(request.ExactImageId);
        args.AddRange(request.WaitingCommand);
        return args;
    }

    /// <summary>Invariant round-trip-safe CPU formatting: 0.25 stays "0.25", 4 stays "4".</summary>
    internal static string FormatCpus(double cpus) =>
        cpus.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task StartContainerAsync(string containerId, CancellationToken ct = default)
    {
        DockerValidation.RequireContainerId(containerId, "start container id");
        var inv = new DockerCliInvocation(["start", containerId], timeout: TimeSpan.FromMinutes(2));
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "container start");
    }

    /// <summary>Typed container state inspect. The returned Id must match the requested
    /// container and the Image must be well formed; a mismatched identity fails closed.</summary>
    public async Task<DockerContainerState> InspectContainerAsync(
        string containerId, string? expectedImageId = null, CancellationToken ct = default)
    {
        DockerValidation.RequireContainerId(containerId, "inspect container id");
        var inv = new DockerCliInvocation(["inspect", containerId]);
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "container inspect");
        var state = DockerContainerState.FromJson(
            System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
        if (!string.Equals(state.ContainerId, containerId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "docker inspect returned a different container id than requested; refusing mismatched identity.");
        if (expectedImageId is { } expected &&
            !string.Equals(state.ImageId, expected, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the running container's image does not match the resolved exact image id; " +
                "refusing identity mismatch.");
        return state;
    }

    /// <summary>Full typed inspect (effective HostConfig/Config) for preflight verification.</summary>
    public async Task<DockerContainerDetailed> InspectContainerDetailedAsync(
        string containerId, CancellationToken ct = default)
    {
        DockerValidation.RequireContainerId(containerId, "inspect container id");
        var inv = new DockerCliInvocation(["inspect", containerId]);
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "container inspect");
        return DockerContainerDetailed.FromJson(
            System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
    }

    /// <summary>Typed inspect that returns null ONLY when absence is positively established
    /// ("No such object"/"No such container" on a non-timeout exit failure). Any operational
    /// failure (timeout, cancellation, truncation, malformed output) throws.</summary>
    public async Task<DockerContainerState?> TryInspectContainerAsync(
        string containerId, CancellationToken ct = default)
    {
        DockerValidation.RequireContainerId(containerId, "inspect container id");
        var inv = new DockerCliInvocation(["inspect", containerId]);
        var result = await _transport.RunAsync(inv, ct);
        if (!result.Succeeded)
        {
            if (IndicatesAbsent(result, "No such object") || IndicatesAbsent(result, "No such container"))
                return null;
            throw new InvalidOperationException(
                $"docker container inspect failed ({DescribeFailure(result)}).");
        }
        var state = DockerContainerState.FromJson(
            System.Text.Encoding.UTF8.GetBytes(result.StandardOutput));
        if (!string.Equals(state.ContainerId, containerId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "docker inspect returned a different container id than requested; refusing mismatched identity.");
        return state;
    }

    public async Task<bool> ContainerExistsAsync(string containerId, CancellationToken ct = default) =>
        await TryInspectContainerAsync(containerId, ct) is not null;

    /// <summary>Typed exec of one validated command. Duration is measured monotonically.
    ///
    /// Capture contract: the returned <see cref="SandboxCommandResult"/> carries the COMPLETE
    /// transport-captured output unchanged — the transport's combined capture cap is the only
    /// bound. This adapter deliberately applies NO intermediate presentation tail: decision
    /// inputs (zero-test detection, failure classification) must see everything that was
    /// captured, and presentation shortening happens only later, after classification and
    /// sanitization. Flagged operational outcomes keep their flags; a synthetic negative exit
    /// with no flag (startup/I/O failure) passes through unchanged for the Tester boundary to
    /// classify as an infrastructure failure.</summary>
    internal async Task<SandboxCommandResult> ExecAsync(DockerExecRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        DockerValidation.RequireContainerId(request.ContainerId, "exec container id");
        if (!SandboxCommand.IsSafeGuestWorkingDirectory(request.WorkingDirectory))
            throw new InvalidOperationException(
                "exec working directory must be exactly /workspace or strictly beneath it.");
        if (request.Timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("exec timeout must be positive.");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var inv = new DockerCliInvocation(
            BuildExecArguments(request),
            stdIn: request.StdIn,
            maxOutputBytes: request.MaxOutputBytes,
            timeout: request.Timeout);
        var result = await _transport.RunAsync(inv, ct);
        sw.Stop();

        return new SandboxCommandResult(
            ExitCode: result.TimedOut || result.Cancelled || result.OutputTruncated
                ? -1 : result.ExitCode,
            StdOutTail: result.StandardOutput,
            StdErrTail: result.StandardError,
            TimedOut: result.TimedOut,
            Cancelled: result.Cancelled,
            OomKilled: false,
            OutputTruncated: result.OutputTruncated,
            Duration: sw.Elapsed);
    }

    /// <summary>Deterministic exec argument vector (asserted in full by tests).</summary>
    internal static IReadOnlyList<string> BuildExecArguments(DockerExecRequest request)
    {
        var args = new List<string> { "exec" };
        if (request.StdIn is not null)
            args.Add("--interactive");
        args.Add("--workdir");
        args.Add(request.WorkingDirectory);
        foreach (var kv in request.Environment.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            args.Add("--env");
            args.Add($"{kv.Key}={kv.Value}");
        }
        args.Add(request.ContainerId);
        args.Add(request.Executable);
        args.AddRange(request.Arguments);
        return args;
    }

    public async Task StopContainerAsync(
        string containerId, TimeSpan stopTimeout, CancellationToken ct = default)
    {
        DockerValidation.RequireContainerId(containerId, "stop container id");
        if (stopTimeout <= TimeSpan.Zero || stopTimeout > TimeSpan.FromMinutes(5))
            throw new InvalidOperationException("stop grace period must be within (0, 5 min].");
        var seconds = ((int)Math.Ceiling(stopTimeout.TotalSeconds))
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var inv = new DockerCliInvocation(
            ["stop", "--time", seconds, containerId],
            timeout: stopTimeout + TimeSpan.FromSeconds(10));
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "container stop");
    }

    public async Task KillContainerAsync(string containerId, CancellationToken ct = default)
    {
        DockerValidation.RequireContainerId(containerId, "kill container id");
        var inv = new DockerCliInvocation(["kill", containerId], timeout: TimeSpan.FromSeconds(30));
        var result = await _transport.RunAsync(inv, ct);
        EnsureSuccess(result, "container kill");
    }

    /// <summary>
    /// Typed `docker rm --force` with a REQUIRED final absence proof. After a successful rm
    /// OR a "No such container/object" response, a typed inspect must positively establish
    /// absence: a container that still exists is a contradiction and throws a cleanup
    /// failure; an operational inspect failure throws; cleanup is never reported successful
    /// without proof. Returns true when removed, false when positively already absent.
    /// </summary>
    public async Task<bool> RemoveContainerAsync(string containerId, CancellationToken ct = default)
    {
        DockerValidation.RequireContainerId(containerId, "remove container id");
        var inv = new DockerCliInvocation(["rm", "--force", containerId], timeout: TimeSpan.FromSeconds(30));
        var result = await _transport.RunAsync(inv, ct);
        var removed = false;
        if (!result.Succeeded)
        {
            if (!(IndicatesAbsent(result, "No such container") || IndicatesAbsent(result, "No such object")))
                throw new InvalidOperationException(
                    $"docker rm failed ({DescribeFailure(result)}).");
        }
        else
        {
            removed = true;
        }

        // Final absence proof applies to BOTH paths — rm success and already-absent claim.
        DockerContainerState? state;
        try
        {
            state = await TryInspectContainerAsync(containerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"docker rm reported {(removed ? "success" : "the container already absent")} but " +
                "the confirming inspect failed operationally; removal cannot be proven: " + ex.Message, ex);
        }
        if (state is not null)
            throw new InvalidOperationException(
                "cleanup contradiction: docker rm reported " +
                (removed ? "success" : "the container already absent") +
                " but a typed inspect still finds the container present; removal is unproven " +
                "and must not be treated as successful cleanup.");
        return removed;
    }

    /// <summary>Lists containers by the COMPLETE scoped Tenninety management identity.
    /// Empty, partial, unknown or unsafe label sets are rejected by the scope type.</summary>
    internal async Task<IReadOnlyList<string>> ListContainersAsync(
        DockerContainerScope scope, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return await ListContainersByLabelsAsync(scope.Labels, ct);
    }

    internal Task<IReadOnlyList<string>> ListRecoveryContainersAsync(
        DockerRecoveryScope scope, CancellationToken ct = default) =>
        ListContainersByLabelsAsync(scope.Labels, ct);

    private async Task<IReadOnlyList<string>> ListContainersByLabelsAsync(
        IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var args = new List<string> { "ps", "--all", "--no-trunc", "--quiet" };
        foreach (var kv in labels.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            args.Add("--filter");
            args.Add($"label={kv.Key}={kv.Value}");
        }
        args.Add("--format");
        args.Add("{{.ID}}");

        var result = await _transport.RunAsync(new DockerCliInvocation(args), ct);
        EnsureSuccess(result, "container recovery list");
        var ids = new List<string>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = line.Trim();
            if (id.Length == 0) continue;
            DockerValidation.RequireContainerId(id, "container recovery list output");
            ids.Add(id);
            if (ids.Count > 10_000)
                throw new InvalidOperationException(
                    "container recovery list exceeded the bounded resource count.");
        }
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException(
                "container recovery list returned duplicate container ids.");
        return ids;
    }

    // ---- failure classification -------------------------------------------------

    /// <summary>Positive absence is claimed ONLY for a non-timeout, non-cancelled,
    /// non-truncated failure whose stderr carries the daemon's exact absence phrase. The
    /// phrase is matched case-insensitively: modern Docker CLIs print the same messages in
    /// lowercase (e.g. "error: no such object: <id>") while older ones capitalise them;
    /// the words required are identical in both spellings.</summary>
    private static bool IndicatesAbsent(DockerCliResult result, string needle) =>
        !result.Succeeded &&
        !result.TimedOut && !result.Cancelled && !result.OutputTruncated &&
        result.StandardError.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Modern daemons report a missing network as
    /// "Error response from daemon: network &lt;name&gt; not found" instead of the older
    /// "No such network" phrasing. Both are positively recognized; anything else (including
    /// an operational failure that happens to mention a network) is not absence.</summary>
    private static bool IndicatesNetworkNotFound(DockerCliResult result) =>
        !result.Succeeded &&
        !result.TimedOut && !result.Cancelled && !result.OutputTruncated &&
        result.StandardError.Contains("network", StringComparison.OrdinalIgnoreCase) &&
        result.StandardError.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static string DescribeFailure(DockerCliResult result) =>
        result.TimedOut ? "timed out"
        : result.Cancelled ? "cancelled"
        : result.OutputTruncated ? "output truncated"
        : $"exit {result.ExitCode}";

    private void EnsureSuccess(DockerCliResult result, string operation, string? scrub = null)
    {
        if (result.Succeeded) return;
        // A bounded, failure-only diagnostic excerpt of the daemon's stderr for exception
        // messages. This is NOT part of the command capture contract (which is preserved
        // complete through ExecAsync) and never reaches Tester decision inputs; Tester public
        // diagnostics reduce exceptions to controlled categories and never copy this text.
        var err = Tail(result.StandardError, 512);
        if (!string.IsNullOrEmpty(scrub)) err = err.Replace(scrub, "[workspace]");
        var dockerHost = _environmentLookup("DOCKER_HOST");
        if (!string.IsNullOrEmpty(dockerHost)) err = err.Replace(dockerHost, "[docker-host]");
        throw new InvalidOperationException(
            $"docker {operation} failed ({DescribeFailure(result)})." +
            (err.Length > 0 ? " " + err : ""));
    }

    /// <summary>Internal bounded presentation helper for failure messages ONLY. It is not part
    /// of the command capture path: captured command output is never shortened here.</summary>
    internal static string Tail(string value, int maxLength)
    {
        if (value.Length <= maxLength) return value;
        return value[^maxLength..];
    }
}
