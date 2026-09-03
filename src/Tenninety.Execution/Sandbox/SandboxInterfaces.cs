namespace Tenninety.Execution.Sandbox;

/// <summary>Creates hardened, disposable sandbox sessions. Implementations must enforce the
/// fixed <see cref="SandboxPolicy"/> and the spec's
/// limits; nothing here is reachable from untrusted content. This is trusted orchestration
/// surface: <see cref="SandboxSpec"/> (including its excluded host scratch path) may flow in,
/// but only the sanitized <see cref="SandboxSessionInfo"/> may flow out to sessions.</summary>
public interface ISandboxRuntime
{
    /// <summary>Creates and starts a fresh container for the given validated spec.</summary>
    Task<ISandboxSession> CreateAsync(SandboxSpec spec, CancellationToken ct = default);
}

/// <summary>
/// One live sandbox container. The agent/session-facing contract exposes only sanitized data
/// (<see cref="Info"/>: session ID, role, state, /workspace path) — never the host scratch
/// path from the spec and never the authoritative repository path.
/// </summary>
public interface ISandboxSession : IAsyncDisposable
{
    /// <summary>Sanitized session identity and lifecycle view. Contains no host paths.</summary>
    SandboxSessionInfo Info { get; }

    SandboxSessionState State => Info.State;

    /// <summary>True only once the container is confirmed no longer running; trusted
    /// extraction must refuse to scan a workspace before this holds.</summary>
    bool WritesQuiescent => Info.State == SandboxSessionState.StoppedQuiescent;

    /// <summary>Runs one bounded command inside the container via a structured exec.</summary>
    Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken ct = default);

    /// <summary>Bounded graceful stop with a kill fallback; confirms quiescence before returning.</summary>
    Task StopAsync(CancellationToken ct = default);
}

/// <summary>
/// Seam between typed argument construction and the actual Docker CLI process. The production
/// transport executes `docker` with ArgumentList only and never a host shell; fakes
/// implement this interface for deterministic unit tests of typed argument vectors. There is
/// deliberately no overload accepting a single shell command string.
///
/// The single frozen <see cref="DockerCliInvocation"/> replaces loose primitives so stdin, output
/// caps, per-invocation timeouts and cancellation are all enforced structurally.
/// </summary>
public interface IDockerCliTransport
{
    /// <summary>Runs the Docker CLI with the given structured, trusted invocation. No raw shell
    /// or joined command string is ever accepted or reachable.</summary>
    Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default);
}

/// <summary>
/// Frozen typed invocation for one Docker CLI call. Every field is validated on construction;
/// no caller-provided raw shell string, unsafe timeout, unbounded output, or oversized stdin
/// can ever reach the transport. Stdin is bounded by UTF-8 byte count (not UTF-16 length).
/// </summary>
public sealed class DockerCliInvocation
{
    /// <summary>Hard bound on stdin payload measured in UTF-8 bytes.</summary>
    public const long MaxStdInBytes = 1_048_576;

    private readonly IReadOnlyList<string> _arguments;

    public DockerCliInvocation(
        IReadOnlyList<string> arguments,
        string? stdIn = null,
        long maxOutputBytes = 1_048_576,
        TimeSpan? timeout = null)
    {
        if (arguments is null || arguments.Count == 0)
            throw new ArgumentException("docker CLI invocation must carry at least one argument.", nameof(arguments));
        if (arguments.Any(a => a is null))
            throw new ArgumentException("docker CLI invocation arguments must not contain null elements.", nameof(arguments));
        if (arguments.Any(a => a.Contains('\0')))
            throw new ArgumentException("docker CLI invocation arguments must not contain NUL bytes.", nameof(arguments));
        if (maxOutputBytes <= 0 || maxOutputBytes > 64L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maxOutputBytes),
                $"maxOutputBytes {maxOutputBytes} is outside (0, 64 MiB].");
        if (timeout is { } t && (t <= TimeSpan.Zero || t > TimeSpan.FromHours(24)))
            throw new ArgumentOutOfRangeException(nameof(timeout),
                "docker CLI invocation timeout must be within (0, 24 h].");
        if (stdIn is not null &&
            System.Text.Encoding.UTF8.GetByteCount(stdIn) > MaxStdInBytes)
            throw new ArgumentOutOfRangeException(nameof(stdIn),
                $"docker CLI invocation stdin exceeds the {MaxStdInBytes}-byte UTF-8 cap.");
        _arguments = new List<string>(arguments).AsReadOnly();
        Arguments = _arguments;
        StdIn = stdIn;
        MaxOutputBytes = maxOutputBytes;
        Timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public IReadOnlyList<string> Arguments { get; }
    public string? StdIn { get; }
    public long MaxOutputBytes { get; }
    public TimeSpan Timeout { get; }
}

/// <summary>Bounded, structured result of one Docker CLI invocation.</summary>
public sealed record DockerCliResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    bool OutputTruncated,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled && !OutputTruncated;
}
