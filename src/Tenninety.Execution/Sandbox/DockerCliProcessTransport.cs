using System.Diagnostics;
using System.Text;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Production Docker CLI process transport. Starts only the trusted Docker executable via
/// <c>ProcessStartInfo.ArgumentList</c> — never a host shell, never a PATH search (PATH is
/// attacker-controllable; only fixed absolute locations are accepted).
///
/// Guarantees:
///  - output readers start immediately; bounded stdin is written concurrently and stdin is
///    always closed, so a child waiting for stdin EOF can never deadlock the invocation;
///  - stdout and stderr use independent buffers; the combined retained size can never exceed
///    the invocation cap, and overflow is detected by actual over-consumption (output of
///    exactly cap bytes is retained, not truncated);
///  - overflow triggers immediate cancellation and a process-tree kill rather than waiting
///    for EOF or the timeout;
///  - timeout, caller cancellation, truncation, startup failure, and I/O failure are
///    reported as distinct, accurate flags (a startup failure is never labelled cancelled);
///  - the child process tree is killed and reaped on timeout, cancellation, truncation,
///    and reader/writer failure; all tasks finish, nothing leaks;
///  - the child environment is cleared and rebuilt from an ISOLATED per-instance Docker
///    client configuration: HOME and DOCKER_CONFIG point at transport-owned empty
///    directories (never the host's), so the host Docker client config — including any
///    proxy settings the CLI would otherwise convert into container environment variables
///    during `docker create` — can never influence the daemon connection or the created
///    container. An explicit host DOCKER_CONTEXT fails closed; only an explicit DOCKER_HOST
///    (plus DOCKER_CERT_PATH, DOCKER_TLS_VERIFY and XDG_RUNTIME_DIR) is honoured;
///  - retained output, exception messages, and error payloads are bounded.
/// </summary>
public sealed class DockerCliProcessTransport : IDockerCliTransport, IDisposable
{
    /// <summary>Absolute locations trusted to contain the Docker CLI. PATH is never searched.</summary>
    private static readonly string[] TrustedDockerExecutableCandidates =
        ["/usr/bin/docker", "/usr/local/bin/docker"];

    /// <summary>
    /// Deterministic trusted path for system tools the Docker CLI may need to launch.
    /// The host PATH is attacker-influenced and is never copied.
    /// </summary>
    internal const string TrustedClientPath = "/usr/local/bin:/usr/bin:/bin";

    /// <summary>
    /// Explicit Docker-daemon connection variables copied EXACTLY (never by prefix) from the
    /// host when set. HOME, DOCKER_CONFIG, DOCKER_CONTEXT and PATH are deliberately absent:
    /// HOME/DOCKER_CONFIG always point at instance-owned isolated directories, an explicit
    /// DOCKER_CONTEXT fails closed, and PATH uses the deterministic trusted value above.
    /// </summary>
    internal static readonly string[] ClientConnectionKeys =
        ["DOCKER_HOST", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "XDG_RUNTIME_DIR"];

    /// <summary>Proxy variables that must NEVER reach the Docker CLI child: the CLI reads
    /// proxy configuration from the client config.json and `docker create` can convert it
    /// into container environment variables, leaking credentials or private hostnames.</summary>
    internal static readonly string[] ProxyEnvironmentKeys =
    [
        "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy",
        "FTP_PROXY", "ftp_proxy", "NO_PROXY", "no_proxy",
        "ALL_PROXY", "all_proxy",
    ];

    private readonly IDockerProcessStarter _starter;
    private readonly string _workingDirectory;
    private readonly string _dockerExecutable;
    private readonly bool _useIsolatedClientEnvironment;
    private readonly string _isolatedHome;
    private readonly string _isolatedDockerConfig;
    private readonly Func<string, string?> _hostEnvironmentLookup;
    private bool _disposed;

    /// <summary>Production constructor. Resolves the Docker executable from fixed absolute
    /// locations (fail closed — no PATH search), creates a per-instance trusted working
    /// directory AND a per-instance isolated Docker client configuration (empty home +
    /// empty DOCKER_CONFIG directory) that only this instance owns and deletes.</summary>
    public DockerCliProcessTransport()
        : this(RealDockerProcessStarter.Instance, CreatePerInstanceWorkingDirectory(),
               ResolveTrustedDockerExecutable(), useIsolatedClientEnvironment: true)
    {
    }

    /// <summary>Seam constructor for deterministic unit tests (InternalsVisibleTo). The test
    /// seam may inject a harmless fake executable path without launching it. Tests can opt
    /// out of the isolation setup to script exact child environments directly.</summary>
    internal DockerCliProcessTransport(
        IDockerProcessStarter starter,
        string workingDirectory,
        string dockerExecutable,
        bool useIsolatedClientEnvironment = true,
        Func<string, string?>? hostEnvironmentLookup = null)
    {
        _starter = starter ?? throw new ArgumentNullException(nameof(starter));
        _hostEnvironmentLookup = hostEnvironmentLookup ?? Environment.GetEnvironmentVariable;
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("a trusted working directory is required.", nameof(workingDirectory));
        if (string.IsNullOrWhiteSpace(dockerExecutable))
            throw new ArgumentException("a trusted docker executable path is required.", nameof(dockerExecutable));
        _workingDirectory = Directory.CreateDirectory(workingDirectory).FullName;
        _dockerExecutable = dockerExecutable;
        _useIsolatedClientEnvironment = useIsolatedClientEnvironment;
        _isolatedHome = Path.Combine(_workingDirectory, "client-home");
        _isolatedDockerConfig = Path.Combine(_workingDirectory, "client-docker-config");
        if (useIsolatedClientEnvironment)
        {
            Directory.CreateDirectory(_isolatedHome);
            Directory.CreateDirectory(_isolatedDockerConfig);
        }
    }

    public async Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sw = Stopwatch.StartNew();

        if (ct.IsCancellationRequested)
            return new DockerCliResult(-1, "", "", TimedOut: false, Cancelled: true,
                OutputTruncated: false, Duration: sw.Elapsed);

        var clientEnvironment = _useIsolatedClientEnvironment
            ? BuildIsolatedClientEnvironment(_hostEnvironmentLookup,
                _isolatedHome, _isolatedDockerConfig)
            : BuildClientEnvironment(_hostEnvironmentLookup);
        var config = new DockerCliProcessConfig(
            FileName: _dockerExecutable,
            Arguments: invocation.Arguments,
            Environment: clientEnvironment,
            WorkingDirectory: _workingDirectory,
            RedirectStdin: true);

        IDockerProcess proc;
        try
        {
            proc = _starter.Start(config);
        }
        catch (Exception ex)
        {
            sw.Stop();
            // The pre-start cancellation check already passed, so this is a genuine STARTUP
            // failure: it must never be classified as caller cancellation or a timeout, even
            // if the caller token becomes cancelled during the start attempt. The bounded
            // message names only the exception type — never arbitrary exception text.
            return new DockerCliResult(
                ExitCode: -1, StandardOutput: "", StandardError: BoundedMessage(
                    "docker process failed to start: " + ex.GetType().Name),
                TimedOut: false, Cancelled: false,
                OutputTruncated: false, Duration: sw.Elapsed);
        }

        using var timeoutCts = new CancellationTokenSource(invocation.Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var budget = new SharedOutputBudget(invocation.MaxOutputBytes);
        var stdoutSink = new MemoryStream();   // independent per-reader storage
        var stderrSink = new MemoryStream();   // independent per-reader storage
        var overflowFlag = 0L;
        string? ioFailure = null;
        var ioGate = new object();

        void RecordIoFailure(string what, Exception ex)
        {
            // Atomically record the FIRST failure and terminate the whole invocation
            // immediately — the process must never be allowed to run until the invocation
            // timeout after a reader/writer/wait failure.
            lock (ioGate)
            {
                if (ioFailure is not null) return;
                ioFailure = BoundedMessage($"{what} failure: {ex.GetType().Name}");
                try { linkedCts.Cancel(); } catch { }
                KillProcessTree();
            }
        }

        void KillProcessTree()
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* reap follows */ }
        }

        async Task ReadLoopAsync(Stream stream, MemoryStream sink, string streamName)
        {
            var buffer = new byte[64 * 1024];   // independent per-reader buffer
            while (true)
            {
                int read;
                try { read = await stream.ReadAsync(buffer, linkedCts.Token); }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested) { return; }
                catch (Exception ex) { RecordIoFailure(streamName, ex); return; }
                if (read == 0) return; // EOF

                var retained = budget.TryReserve(read);
                if (retained > 0) sink.Write(buffer, 0, retained);
                if (retained < read)
                {
                    // Actual overflow: retained total would exceed the cap. Terminate promptly.
                    if (Interlocked.Exchange(ref overflowFlag, 1L) == 0L)
                    {
                        try { linkedCts.Cancel(); } catch { }
                        KillProcessTree();
                    }
                    return;
                }
            }
        }

        async Task WriteStdinAsync()
        {
            try
            {
                if (invocation.StdIn is { } payload)
                {
                    var bytes = Encoding.UTF8.GetBytes(payload);   // bounded: validated at construction
                    await proc.StandardInput.BaseStream.WriteAsync(bytes, linkedCts.Token);
                    await proc.StandardInput.BaseStream.FlushAsync(linkedCts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RecordIoFailure("stdin", ex); }
            finally
            {
                // Always close stdin: with no payload this delivers immediate EOF so a child
                // waiting for stdin can never hang the invocation.
                try { proc.StandardInput.Close(); } catch { }
            }
        }

        async Task WaitExitAsync()
        {
            try { await proc.WaitForExitAsync(linkedCts.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RecordIoFailure("wait", ex); }
        }

        // Readers start immediately; stdin writing and the exit wait run concurrently with
        // them. Nothing waits for EOF before writing stdin — no circular dependency.
        // Every path through the invocation disposes the process exactly once after all
        // reader/writer/wait/reap work has finished.
        try
        {
            var stdoutTask = Task.Run(() => ReadLoopAsync(proc.StandardOutput.BaseStream, stdoutSink, "stdout"));
            var stderrTask = Task.Run(() => ReadLoopAsync(proc.StandardError.BaseStream, stderrSink, "stderr"));
            var stdinTask = Task.Run(WriteStdinAsync);
            var waitTask = Task.Run(WaitExitAsync);

            await Task.WhenAll(stdoutTask, stderrTask, stdinTask, waitTask);

            var overflow = Interlocked.Read(ref overflowFlag) == 1L;
            var timedOut = !overflow && timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested;
            var cancelled = !overflow && !timedOut && ct.IsCancellationRequested;

            if (overflow || timedOut || cancelled || ioFailure is not null || !proc.HasExited)
                KillAndReap(proc);

            sw.Stop();
            var terminated = overflow || timedOut || cancelled || ioFailure is not null;
            var exitCode = terminated ? -1 : proc.HasExited ? proc.ExitCode ?? -1 : -1;
            var stderrText = ioFailure ?? Encoding.UTF8.GetString(stderrSink.ToArray());

            return new DockerCliResult(
                ExitCode: exitCode,
                StandardOutput: Encoding.UTF8.GetString(stdoutSink.ToArray()),
                StandardError: stderrText,
                TimedOut: timedOut,
                Cancelled: cancelled,
                OutputTruncated: overflow,
                Duration: sw.Elapsed);
        }
        finally
        {
            // Reap once more defensively, then dispose process + redirected stream handles.
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExitAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); } catch { }
            try { proc.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Builds the ISOLATED Docker-client child environment:
    ///  - the environment starts empty (never inherits the host environment);
    ///  - HOME points at the instance-owned isolated home, DOCKER_CONFIG at the
    ///    instance-owned isolated Docker configuration directory — the host client
    ///    configuration file is never copied, read or referenced, so no proxy,
    ///    credential-helper, plugin or context configuration can influence the CLI;
    ///  - PATH is a small deterministic trusted value, never the host PATH;
    ///  - an explicitly-set host DOCKER_CONTEXT fails closed (this hardened transport
    ///    requires an explicit DOCKER_HOST to name the intended daemon);
    ///  - only the explicit connection variables are copied exactly when set.
    /// Internal so tests can drive it with a fully controlled host lookup.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildIsolatedClientEnvironment(
        Func<string, string?> lookup,
        string isolatedHome,
        string isolatedDockerConfig)
    {
        // Fail closed on an explicit host DOCKER_CONTEXT: silently ignoring it could connect
        // to an unintended daemon; this transport requires an explicit DOCKER_HOST instead.
        var dockerContext = lookup("DOCKER_CONTEXT");
        if (!string.IsNullOrEmpty(dockerContext))
            throw new InvalidOperationException(
                "an explicit host DOCKER_CONTEXT is set; this hardened Docker client transport " +
                "requires an explicit DOCKER_HOST to name the intended daemon because the " +
                "host context configuration is never read.");

        var result = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = isolatedHome,
            ["DOCKER_CONFIG"] = isolatedDockerConfig,
            ["PATH"] = TrustedClientPath,
        };
        foreach (var key in ClientConnectionKeys)
        {
            var value = lookup(key);
            if (!string.IsNullOrEmpty(value)) result[key] = value;
        }
        return result;
    }

    /// <summary>Legacy allowlist builder retained ONLY for the non-isolated seam mode used by
    /// a few transport unit tests; production always uses the isolated environment.</summary>
    internal static IReadOnlyDictionary<string, string> BuildClientEnvironment(
        Func<string, string?> lookup)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ClientConnectionKeys)
        {
            var value = lookup(key);
            if (!string.IsNullOrEmpty(value)) result[key] = value;
        }
        return result;
    }

    private static string BoundedMessage(string message)
    {
        const int max = 512;
        if (message.Length <= max) return message;
        return message[..max] + "…[bounded]";
    }

    private static void KillAndReap(IDockerProcess proc)
    {
        try { proc.Kill(entireProcessTree: true); } catch { }
        try { proc.WaitForExitAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    private static string CreatePerInstanceWorkingDirectory()
    {
        // Per-instance lifetime: the working directory (containing the isolated home and
        // Docker configuration) is owned by exactly one transport; disposing one instance
        // never affects another. It is created independently, under the process's system
        // temporary location (Path.GetTempPath()), and is never derived from a sandbox
        // workspace path.
        var dir = Path.Combine(Path.GetTempPath(), $"tenninety-docker-cli-wd-{Guid.NewGuid():N}");
        return Directory.CreateDirectory(dir).FullName;
    }

    private static string ResolveTrustedDockerExecutable()
    {
        foreach (var candidate in TrustedDockerExecutableCandidates)
            if (File.Exists(candidate))
                return candidate;
        throw new InvalidOperationException(
            "the trusted Docker CLI executable was not found at the known absolute locations (" +
            string.Join(", ", TrustedDockerExecutableCandidates) + "); PATH is deliberately never " +
            "searched because it is attacker-controllable. Install Docker at a known location.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, recursive: true);
        }
        catch { /* best-effort deletion of our own per-instance directory */ }
    }
}

/// <summary>
/// Atomic combined-output budget. Two concurrent readers reserve bytes under one lock so the
/// sum of retained stdout and retained stderr can never exceed the cap, and no reader can
/// consume the other's allowance. Overflow is a real over-consumption event, never an
/// "exactly at cap" false positive.
/// </summary>
internal sealed class SharedOutputBudget
{
    private readonly object _gate = new();
    private readonly long _cap;
    private long _retained;

    public SharedOutputBudget(long cap) => _cap = cap;

    /// <summary>Reserves up to <paramref name="requested"/> bytes of the remaining allowance.
    /// Returns how many bytes may be retained; a return value smaller than the request means
    /// the output overflowed the cap.</summary>
    public int TryReserve(int requested)
    {
        lock (_gate)
        {
            var remaining = _cap - _retained;
            if (remaining <= 0) return 0;
            var take = (int)Math.Min(requested, remaining);
            _retained += take;
            return take;
        }
    }
}

/// <summary>Mockable process-launch seam for deterministic unit tests of the production
/// transport itself (no test-only transport duplicate exists).</summary>
public interface IDockerProcessStarter
{
    IDockerProcess Start(DockerCliProcessConfig config);
}

public sealed record DockerCliProcessConfig(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string WorkingDirectory,
    bool RedirectStdin);

public interface IDockerProcess : IDisposable
{
    StreamWriter StandardInput { get; }
    StreamReader StandardOutput { get; }
    StreamReader StandardError { get; }
    int? ExitCode { get; }
    bool HasExited { get; }
    void Kill(bool entireProcessTree = true);
    Task WaitForExitAsync(CancellationToken ct);
}

/// <summary>The real process starter used by the production constructor.</summary>
internal sealed class RealDockerProcessStarter : IDockerProcessStarter
{
    public static readonly RealDockerProcessStarter Instance = new();

    public IDockerProcess Start(DockerCliProcessConfig config)
    {
        var proc = Process.Start(BuildStartInfo(config))
            ?? throw new InvalidOperationException("process start returned null.");
        return new RealDockerProcess(proc);
    }

    /// <summary>Pure builder, internal so tests can assert shell-free ArgumentList
    /// configuration without launching anything.</summary>
    internal static ProcessStartInfo BuildStartInfo(DockerCliProcessConfig config)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.FileName,
            WorkingDirectory = config.WorkingDirectory,
            RedirectStandardInput = true,   // always redirect: closed immediately when unused
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,        // never a host shell
            CreateNoWindow = true,
        };
        psi.Environment.Clear();
        foreach (var kv in config.Environment) psi.Environment[kv.Key] = kv.Value;
        foreach (var arg in config.Arguments) psi.ArgumentList.Add(arg);
        return psi;
    }
}

internal sealed class RealDockerProcess : IDockerProcess
{
    private readonly Process _proc;

    public RealDockerProcess(Process proc) => _proc = proc;

    public StreamWriter StandardInput => _proc.StandardInput;
    public StreamReader StandardOutput => _proc.StandardOutput;
    public StreamReader StandardError => _proc.StandardError;
    public int? ExitCode => _proc.HasExited ? _proc.ExitCode : null;
    public bool HasExited => _proc.HasExited;
    public void Kill(bool entireProcessTree = true) => _proc.Kill(entireProcessTree);
    public async Task WaitForExitAsync(CancellationToken ct) => await _proc.WaitForExitAsync(ct);
    public void Dispose() => _proc.Dispose();
}
