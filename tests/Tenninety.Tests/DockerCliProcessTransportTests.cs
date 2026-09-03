using System.Collections.Concurrent;
using System.Text;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>Fake process options for scripting the production transport's process seam.</summary>
public sealed class FakeProcessOptions
{
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    /// <summary>Maximum bytes each fake stream delivers per read (small values exercise loops).</summary>
    public int StdOutChunkSize { get; init; } = 64 * 1024;
    public int StdErrChunkSize { get; init; } = 64 * 1024;
    /// <summary>Delay before EOF is delivered (simulates a still-running child).</summary>
    public TimeSpan EofDelay { get; init; } = TimeSpan.Zero;
    public int ExitCode { get; init; }
    /// <summary>Delay before the process naturally exits.</summary>
    public TimeSpan ExitDelay { get; init; } = TimeSpan.FromMilliseconds(5);
    /// <summary>Deliver stdout/stderr (and EOF) only after the transport closed stdin:
    /// proves a child waiting for stdin EOF can never deadlock the transport.</summary>
    public bool GateOnStdinClose { get; init; }
    /// <summary>Throw IOException from the stdout stream after this many successful reads
    /// (reader-failure path). Negative disables.</summary>
    public int StdOutThrowsAfterReads { get; init; } = -1;
}

/// <summary>
/// Fake process used with the PRODUCTION <see cref="DockerCliProcessTransport"/> seam
/// constructor. Records kill/stdin behavior and scripts streams deterministically.
/// </summary>
public sealed class FakeDockerProcess : IDockerProcess
{
    public int DisposeCount { get; private set; }

    private readonly FakeStdinStream _stdin;
    private readonly CancellationTokenSource _killCts = new();
    private readonly TaskCompletionSource _killed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeSpan _exitDelay;
    private readonly int _exitCode;
    private bool _exited;

    public FakeDockerProcess(FakeProcessOptions options)
    {
        _exitDelay = options.ExitDelay;
        _exitCode = options.ExitCode;
        _stdin = new FakeStdinStream();
        StandardInput = new StreamWriter(_stdin) { AutoFlush = true };
        StandardOutput = new StreamReader(new FakeOutputStream(
            Encoding.UTF8.GetBytes(options.StdOut), options.StdOutChunkSize, options.EofDelay,
            options.GateOnStdinClose ? _stdin.ClosedTask : null,
            this, options.StdOutThrowsAfterReads));
        StandardError = new StreamReader(new FakeOutputStream(
            Encoding.UTF8.GetBytes(options.StdErr), options.StdErrChunkSize, options.EofDelay,
            options.GateOnStdinClose ? _stdin.ClosedTask : null,
            this, throwsAfterReads: -1));
    }

    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }

    public int? ExitCode => HasExited ? _exitCode : null;
    public bool HasExited => WasKilled || _exited;
    public bool WasKilled { get; private set; }
    public bool StdinClosed => _stdin.Closed;
    public byte[] StdinWritten => _stdin.Written;
    internal CancellationToken KillToken => _killCts.Token;

    public void Kill(bool entireProcessTree = true)
    {
        WasKilled = true;
        _killCts.Cancel();
        _killed.TrySetResult();
    }

    public void Dispose() => DisposeCount++;

    public async Task WaitForExitAsync(CancellationToken ct)
    {
        if (HasExited) return;
        var delay = _exitDelay == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.Infinite, ct)
            : Task.Delay(_exitDelay, ct);
        var winner = await Task.WhenAny(delay, _killed.Task);
        if (winner != delay) return; // killed
        try
        {
            await delay;
            _exited = true;
        }
        catch (OperationCanceledException)
        {
            // cancellation alone does not exit a real process; the transport kills next
        }
    }
}

/// <summary>Capturing stdin stream whose disposal (Close) signals stdin EOF.</summary>
internal sealed class FakeStdinStream : Stream
{
    private readonly MemoryStream _captured = new();
    private readonly TaskCompletionSource _closedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Closed { get; private set; }
    public byte[] Written => _captured.ToArray();
    public Task ClosedTask => _closedTcs.Task;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !Closed;
    public override long Length => _captured.Length;
    public override long Position { get => _captured.Position; set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (Closed) throw new IOException("stdin already closed");
        await _captured.WriteAsync(buffer, ct);
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Closed)
        {
            Closed = true;
            _closedTcs.TrySetResult();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Scripted output stream: bounded chunks, optional EOF delay, optional stdin-close
/// gate, optional scripted reader failure, and kill-aware termination.</summary>
internal sealed class FakeOutputStream : Stream
{
    private readonly byte[] _content;
    private readonly int _chunkSize;
    private readonly TimeSpan _eofDelay;
    private readonly Task? _gate;
    private readonly FakeDockerProcess _owner;
    private int _readsBeforeThrow;
    private int _position;

    public FakeOutputStream(byte[] content, int chunkSize, TimeSpan eofDelay,
        Task? gate, FakeDockerProcess owner, int throwsAfterReads)
    {
        _content = content;
        _chunkSize = Math.Max(1, chunkSize);
        _eofDelay = eofDelay;
        _gate = gate;
        _owner = owner;
        _readsBeforeThrow = throwsAfterReads;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _content.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_gate is { } gate && !gate.IsCompleted)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _owner.KillToken);
            try { await gate.WaitAsync(linked.Token); }
            catch (OperationCanceledException) { return 0; }
        }
        if (_owner.HasExited || _owner.WasKilled) return 0;

        if (_position >= _content.Length)
        {
            if (_eofDelay > TimeSpan.Zero)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _owner.KillToken);
                try { await Task.Delay(_eofDelay, linked.Token); }
                catch (OperationCanceledException) { return 0; }
            }
            return 0; // EOF
        }

        if (_readsBeforeThrow == 0) throw new IOException("simulated stream failure");
        if (_readsBeforeThrow > 0) _readsBeforeThrow--;

        var take = Math.Min(Math.Min(buffer.Length, _content.Length - _position), _chunkSize);
        _content.AsMemory(_position, take).CopyTo(buffer);
        _position += take;
        return take;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Fake process starter recording every configuration the production transport hands it.</summary>
public sealed class FakeDockerProcessStarter : IDockerProcessStarter
{
    public readonly List<DockerCliProcessConfig> StartedConfigs = new();
    private readonly ConcurrentQueue<Func<DockerCliProcessConfig, FakeDockerProcess>> _scripted = new();
    public Func<DockerCliProcessConfig, FakeDockerProcess>? DefaultFactory { get; set; }
    public Exception? StartException { get; set; }

    public void Enqueue(Func<DockerCliProcessConfig, FakeDockerProcess> factory) =>
        _scripted.Enqueue(factory);

    public IDockerProcess Start(DockerCliProcessConfig config)
    {
        lock (StartedConfigs) StartedConfigs.Add(config);
        if (StartException is { } ex) throw ex;
        if (_scripted.TryDequeue(out var factory)) return factory(config);
        if (DefaultFactory is not null) return DefaultFactory(config);
        return new FakeDockerProcess(new FakeProcessOptions());
    }
}

/// <summary>
/// Tests for the PRODUCTION <see cref="DockerCliProcessTransport"/> through its internal
/// process-launch seam (no test-only transport duplicate exists). Covers stdin deadlock
/// freedom, concurrent output integrity, the strict combined cap, prompt overflow
/// termination, timeout versus cancellation accuracy, process-tree kill, startup failure,
/// reader/writer failure, environment allowlisting, and per-instance disposal.
/// </summary>
public class DockerCliProcessTransportTests : IDisposable
{
    private readonly TempDir _workingDirectory = new();
    private readonly TempDir _otherWorkingDirectory = new();
    private const string FakeDockerPath = "/usr/bin/tenninety-fake-docker";

    public void Dispose()
    {
        _otherWorkingDirectory.Dispose();
        _workingDirectory.Dispose();
    }

    private string WorkingDirectoryRoot => _workingDirectory.Root;

    /// <summary>Builds a transport whose host lookup returns the given values (null default),
    /// so tests control the "host environment" completely without touching the real one.</summary>
    private DockerCliProcessTransport MakeTransport(
        FakeDockerProcessStarter starter,
        Func<string, string?>? lookup = null) =>
        new(starter, _workingDirectory.Root, FakeDockerPath,
            useIsolatedClientEnvironment: true, hostEnvironmentLookup: lookup);

    private static FakeDockerProcess Single(List<FakeDockerProcess> created) => created.Single();

    private static DockerCliResult Ok(string stdout = "", string stderr = "") =>
        new(0, stdout, stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    // ---- input validation -------------------------------------------------------

    [Fact]
    public void Rejects_empty_argument_vector() =>
        Assert.Throws<ArgumentException>(() => new DockerCliInvocation([]));

    [Fact]
    public void Rejects_null_argument_elements() =>
        Assert.Throws<ArgumentException>(() => new DockerCliInvocation(["version", null!]));

    [Fact]
    public void Rejects_nul_bytes_in_arguments() =>
        Assert.Throws<ArgumentException>(() => new DockerCliInvocation(["version", "a\0b"]));

    [Fact]
    public void Rejects_invalid_timeout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DockerCliInvocation(["version"], timeout: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DockerCliInvocation(["version"], timeout: TimeSpan.FromHours(25)));
    }

    [Fact]
    public void Rejects_invalid_output_cap()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DockerCliInvocation(["version"], maxOutputBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DockerCliInvocation(["version"], maxOutputBytes: 65L * 1024 * 1024));
    }

    [Fact]
    public void Rejects_stdin_exceeding_the_utf8_byte_cap_even_when_utf16_length_fits()
    {
        // 600_000 '€' characters: UTF-16 length 600_000 <= 1 MiB, UTF-8 bytes 1_800_000 > 1 MiB.
        var payload = new string('\u20ac', 600_000);
        Assert.True(Encoding.UTF8.GetByteCount(payload) > DockerCliInvocation.MaxStdInBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DockerCliInvocation(["version"], stdIn: payload));
    }

    [Fact]
    public void Valid_invocation_freezes_arguments_and_optional_fields()
    {
        var args = new List<string> { "version" };
        var inv = new DockerCliInvocation(args, stdIn: "hello",
            maxOutputBytes: 1024, timeout: TimeSpan.FromSeconds(5));
        args.Add("--evil");
        Assert.Equal(["version"], inv.Arguments);
        Assert.Equal(1024, inv.MaxOutputBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), inv.Timeout);
    }

    // ---- process configuration ---------------------------------------------------

    [Fact]
    public async Task Production_transport_passes_exact_arguments_via_ArgumentList_and_no_shell()
    {
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions()));
        using var transport = MakeTransport(starter);

        var args = new List<string> { "create", "--name", "a b $(rm -rf /) | ; &'\"", "x" };
        var result = await transport.RunAsync(new DockerCliInvocation(args));

        Assert.True(result.Succeeded);
        var config = starter.StartedConfigs.Single();
        Assert.Equal(FakeDockerPath, config.FileName);              // trusted executable only
        Assert.Equal(args, config.Arguments);                       // exact, one literal element each
        Assert.Equal("a b $(rm -rf /) | ; &'\"", config.Arguments[2]); // metacharacters preserved
        Assert.True(config.RedirectStdin);
    }

    [Fact]
    public void Real_starter_builds_a_shell_free_ArgumentList_process_start_info()
    {
        var config = new DockerCliProcessConfig(
            "/usr/bin/docker",
            ["version", "--format", "{{json .}}"],
            new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
            "/tmp/tenninety-wd",
            RedirectStdin: true);
        var psi = RealDockerProcessStarter.BuildStartInfo(config);
        Assert.False(psi.UseShellExecute);
        Assert.Equal("/usr/bin/docker", psi.FileName);
        Assert.Equal("/tmp/tenninety-wd", psi.WorkingDirectory);
        Assert.Equal(["version", "--format", "{{json .}}"], psi.ArgumentList);
        Assert.True(psi.RedirectStandardInput);
        Assert.True(psi.RedirectStandardOutput);
        Assert.True(psi.RedirectStandardError);
        Assert.Equal("/usr/bin", psi.Environment["PATH"]);
        Assert.DoesNotContain(psi.Environment.Keys, k => k == "LD_PRELOAD");
    }

    // ---- isolated Docker client environment -----------------------------------------

    /// <summary>Unique secret-free sentinel for proving host config content never leaks.</summary>
    private const string ProxySentinelHost = "proxy-sentinel-hostname";

    [Fact]
    public void Isolated_client_environment_never_copies_hostile_host_values()
    {
        var env = DockerCliProcessTransport.BuildIsolatedClientEnvironment(
            key => key switch
            {
                "HOME" => "/host/home",
                "DOCKER_CONFIG" => "/host/.docker",
                "PATH" => "/attacker/controlled/path",
                "DOCKER_HOST" => "unix:///run/docker.sock",
                "DOCKER_CERT_PATH" => "/host/certs",
                "DOCKER_TLS_VERIFY" => "1",
                "XDG_RUNTIME_DIR" => "/run/user/1000",
                "HTTP_PROXY" => "http://user:secret@" + ProxySentinelHost + ":3128",
                "HTTPS_PROXY" => "http://user:secret@" + ProxySentinelHost + ":3128",
                "NO_PROXY" => ProxySentinelHost,
                "all_proxy" => "socks5://" + ProxySentinelHost + ":1080",
                _ => null,
            },
            isolatedHome: "/tmp/iso/home",
            isolatedDockerConfig: "/tmp/iso/docker-config");

        // Host values are never copied.
        Assert.DoesNotContain(env, kv => kv.Value == "/host/home");
        Assert.DoesNotContain(env, kv => kv.Value == "/host/.docker");
        Assert.DoesNotContain(env, kv => kv.Value == "/attacker/controlled/path");
        // Proxy variables never reach the child in any case form.
        foreach (var proxy in DockerCliProcessTransport.ProxyEnvironmentKeys)
            Assert.DoesNotContain(env.Keys, k => k == proxy);
        Assert.DoesNotContain(env.Values, v => v.Contains(ProxySentinelHost));
        // Instance-owned isolation values are used.
        Assert.Equal("/tmp/iso/home", env["HOME"]);
        Assert.Equal("/tmp/iso/docker-config", env["DOCKER_CONFIG"]);
        Assert.Equal(DockerCliProcessTransport.TrustedClientPath, env["PATH"]);
        // Explicit connection values are copied exactly.
        Assert.Equal("unix:///run/docker.sock", env["DOCKER_HOST"]);
        Assert.Equal("/host/certs", env["DOCKER_CERT_PATH"]);
        Assert.Equal("1", env["DOCKER_TLS_VERIFY"]);
        Assert.Equal("/run/user/1000", env["XDG_RUNTIME_DIR"]);
    }

    [Fact]
    public void Isolated_client_environment_contains_exactly_the_expected_keys()
    {
        var env = DockerCliProcessTransport.BuildIsolatedClientEnvironment(
            _ => null, "/tmp/iso/home", "/tmp/iso/docker-config");
        Assert.Equal(["DOCKER_CONFIG", "HOME", "PATH"], env.Keys.OrderBy(k => k).ToList());
        Assert.Equal(DockerCliProcessTransport.TrustedClientPath, env["PATH"]);
        Assert.Equal(["DOCKER_HOST", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "XDG_RUNTIME_DIR"],
            DockerCliProcessTransport.ClientConnectionKeys);
    }

    [Fact]
    public void Explicit_host_docker_context_fails_closed()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DockerCliProcessTransport.BuildIsolatedClientEnvironment(
                key => key == "DOCKER_CONTEXT" ? "host-context" : null,
                "/tmp/iso/home", "/tmp/iso/docker-config"));
        Assert.Contains("DOCKER_CONTEXT", ex.Message);
        Assert.Contains("DOCKER_HOST", ex.Message);
    }

    [Fact]
    public async Task Child_process_receives_the_isolated_client_environment()
    {
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions()));
        using var transport = MakeTransport(starter);
        await transport.RunAsync(new DockerCliInvocation(["version"]));

        var config = starter.StartedConfigs.Single();
        // HOME and DOCKER_CONFIG point INSIDE the instance-owned working directory and the
        // isolated directories exist on disk.
        Assert.StartsWith(WorkingDirectoryRoot, config.Environment["HOME"]);
        Assert.StartsWith(WorkingDirectoryRoot, config.Environment["DOCKER_CONFIG"]);
        Assert.True(Directory.Exists(config.Environment["HOME"]));
        Assert.True(Directory.Exists(config.Environment["DOCKER_CONFIG"]));
        Assert.Equal(DockerCliProcessTransport.TrustedClientPath, config.Environment["PATH"]);
        foreach (var proxy in DockerCliProcessTransport.ProxyEnvironmentKeys)
            Assert.DoesNotContain(config.Environment.Keys, k => k == proxy);
    }

    [Fact]
    public async Task Host_docker_config_file_is_never_copied_or_referenced()
    {
        // A hostile host client config with a proxy sentinel is never copied into the
        // isolated configuration directory and never referenced by the child environment.
        var hostileConfig = Path.Combine(Path.GetTempPath(), $"hostile-docker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostileConfig);
        try
        {
            // The sentinel is a realistic proxy URL shape; the credential fragment is never
            // printed by the test — assertions only check absence.
            var sentinel = "sentinel-" + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(hostileConfig, "config.json"),
                "{\"proxies\":{\"default\":{\"httpProxy\":\"http://u:p@" + sentinel + ":3128\"}}}");

            var starter = new FakeDockerProcessStarter();
            starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions()));
            using var transport = MakeTransport(starter,
                lookup: key => key == "DOCKER_CONFIG" ? hostileConfig : null);
            await transport.RunAsync(new DockerCliInvocation(["version"]));

            var config = starter.StartedConfigs.Single();
            var isolatedConfigDir = config.Environment["DOCKER_CONFIG"];
            Assert.NotEqual(hostileConfig, isolatedConfigDir);
            Assert.False(Directory.EnumerateFileSystemEntries(isolatedConfigDir).Any(),
                "the isolated Docker configuration directory must be empty");
            var isolatedContent = Directory.Exists(isolatedConfigDir)
                ? string.Concat(Directory.EnumerateFiles(isolatedConfigDir, "*", SearchOption.AllDirectories)
                    .Select(File.ReadAllText))
                : "";
            Assert.DoesNotContain(sentinel, isolatedContent);
            Assert.All(config.Environment.Values, v => Assert.DoesNotContain(sentinel, v));
        }
        finally
        {
            try { Directory.Delete(hostileConfig, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Isolated_client_directories_are_distinct_per_transport()
    {
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions()));
        var secondWorking = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"tenninety-distinct-{Guid.NewGuid():N}")).FullName;
        try
        {
            string homeA, configA, homeB, configB;
            using (var first = MakeTransport(starter))
            {
                first.RunAsync(new DockerCliInvocation(["version"])).GetAwaiter().GetResult();
                var config = starter.StartedConfigs[^1];
                homeA = config.Environment["HOME"];
                configA = config.Environment["DOCKER_CONFIG"];
            }
            using (var second = new DockerCliProcessTransport(
                       starter, secondWorking, FakeDockerPath))
            {
                second.RunAsync(new DockerCliInvocation(["version"])).GetAwaiter().GetResult();
                var config = starter.StartedConfigs[^1];
                homeB = config.Environment["HOME"];
                configB = config.Environment["DOCKER_CONFIG"];
            }
            Assert.NotEqual(homeA, homeB);
            Assert.NotEqual(configA, configB);
        }
        finally
        {
            try { Directory.Delete(secondWorking, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Disposing_one_transport_does_not_affect_another_transports_directories()
    {
        var starter = new FakeDockerProcessStarter();
        starter.DefaultFactory = _ => new FakeDockerProcess(new FakeProcessOptions());
        var first = new DockerCliProcessTransport(starter, _workingDirectory.Root, FakeDockerPath);
        var secondWorking = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"tenninety-second-{Guid.NewGuid():N}")).FullName;
        var second = new DockerCliProcessTransport(starter, secondWorking, FakeDockerPath);

        var firstHome = "";
        var secondHome = "";
        try
        {
            first.RunAsync(new DockerCliInvocation(["version"])).GetAwaiter().GetResult();
            second.RunAsync(new DockerCliInvocation(["version"])).GetAwaiter().GetResult();
            firstHome = starter.StartedConfigs[^2].Environment["HOME"];
            secondHome = starter.StartedConfigs[^1].Environment["HOME"];

            first.Dispose();

            // The second transport's isolated home and working directory still exist.
            Assert.True(Directory.Exists(secondHome), "the other transport's home must survive");
            Assert.True(Directory.Exists(secondWorking), "the other transport's working directory must survive");
            // The first transport's directories are deleted with it.
            Assert.False(Directory.Exists(firstHome), "the disposed transport's home must be deleted");
        }
        finally
        {
            second.Dispose();
            Assert.False(Directory.Exists(secondHome));
            try { Directory.Delete(secondWorking, recursive: true); } catch { }
        }
    }

    // ---- normal output -------------------------------------------------------------

    [Fact]
    public async Task Normal_stdout_and_stderr_are_retained_and_success_reported()
    {
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions
        {
            StdOut = "server output",
            StdErr = "warning text",
            StdOutChunkSize = 3,
        }));
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(["version"]));

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("server output", result.StandardOutput);
        Assert.Equal("warning text", result.StandardError);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.OutputTruncated);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    // ---- stdin deadlock freedom -------------------------------------------------------

    [Fact]
    public async Task Child_waiting_for_stdin_eof_receives_stdin_and_completes_without_deadlock()
    {
        var starter = new FakeDockerProcessStarter();
        var created = new List<FakeDockerProcess>();
        starter.Enqueue(_ =>
        {
            var process = new FakeDockerProcess(new FakeProcessOptions
            {
                StdOut = "echoed",
                GateOnStdinClose = true,   // stdout only flows after the transport closed stdin
            });
            lock (created) created.Add(process);
            return process;
        });
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(
            ["exec", "-i", "c1", "cat"], stdIn: "bounded-payload"));

        Assert.True(result.Succeeded);
        Assert.Equal("echoed", result.StandardOutput);
        Assert.True(Single(created).StdinClosed);
        Assert.Equal("bounded-payload", Encoding.UTF8.GetString(Single(created).StdinWritten));
    }

    [Fact]
    public async Task Stdin_is_always_closed_even_without_a_payload()
    {
        var starter = new FakeDockerProcessStarter();
        var created = new List<FakeDockerProcess>();
        starter.Enqueue(_ =>
        {
            var process = new FakeDockerProcess(new FakeProcessOptions());
            lock (created) created.Add(process);
            return process;
        });
        using var transport = MakeTransport(starter);
        await transport.RunAsync(new DockerCliInvocation(["version"]));

        Assert.True(Single(created).StdinClosed);
        Assert.Empty(Single(created).StdinWritten);
    }

    // ---- concurrent output integrity ----------------------------------------------------

    [Fact]
    public async Task Interleaved_concurrent_stdout_and_stderr_cannot_corrupt_bytes()
    {
        var stdout = new string('A', 5000);
        var stderr = new string('B', 5000);
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions
        {
            StdOut = stdout,
            StdErr = stderr,
            StdOutChunkSize = 7,
            StdErrChunkSize = 11,
        }));
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(["version"], maxOutputBytes: 65536));

        Assert.True(result.Succeeded);
        Assert.Equal(stdout, result.StandardOutput);   // no cross-stream contamination
        Assert.Equal(stderr, result.StandardError);
    }

    // ---- strict combined cap -------------------------------------------------------------

    [Fact]
    public async Task Exactly_at_cap_output_is_not_truncated()
    {
        const int cap = 100;
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions
        {
            StdOut = new string('x', cap),
            StdOutChunkSize = 7,
        }));
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(
            ["version"], maxOutputBytes: cap));

        Assert.True(result.Succeeded);
        Assert.False(result.OutputTruncated);
        Assert.Equal(cap, result.StandardOutput.Length);
    }

    [Fact]
    public async Task One_byte_over_cap_is_truncated_and_kills_promptly()
    {
        const int cap = 100;
        var starter = new FakeDockerProcessStarter();
        var created = new List<FakeDockerProcess>();
        starter.Enqueue(_ =>
        {
            var process = new FakeDockerProcess(new FakeProcessOptions
            {
                StdOut = new string('x', cap + 1),
                StdOutChunkSize = 7,
                EofDelay = TimeSpan.FromSeconds(30),   // would hang without the overflow kill
            });
            lock (created) created.Add(process);
            return process;
        });
        using var transport = MakeTransport(starter);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await transport.RunAsync(new DockerCliInvocation(
            ["version"], maxOutputBytes: cap));
        sw.Stop();

        Assert.False(result.Succeeded);
        Assert.True(result.OutputTruncated);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.True(Single(created).WasKilled);
        Assert.True(result.StandardOutput.Length <= cap);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "overflow must terminate promptly");
    }

    [Fact]
    public async Task Racing_readers_cannot_exceed_the_combined_cap_or_corrupt_data()
    {
        const int cap = 1000;
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions
        {
            StdOut = new string('A', 600),
            StdErr = new string('B', 600),
            StdOutChunkSize = 13,
            StdErrChunkSize = 17,
            EofDelay = TimeSpan.FromSeconds(30),
        }));
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(
            ["version"], maxOutputBytes: cap));

        Assert.False(result.Succeeded);
        Assert.True(result.OutputTruncated);
        // Retained stdout plus retained stderr can never exceed the cap.
        Assert.True(result.StandardOutput.Length + result.StandardError.Length <= cap);
        Assert.DoesNotContain('B', result.StandardOutput);
        Assert.DoesNotContain('A', result.StandardError);
    }

    // ---- timeout and cancellation -----------------------------------------------------------

    [Fact]
    public async Task Timeout_is_TimedOut_true_and_Cancelled_false_and_kills_the_process_tree()
    {
        var starter = new FakeDockerProcessStarter();
        var created = new List<FakeDockerProcess>();
        starter.Enqueue(_ =>
        {
            var process = new FakeDockerProcess(new FakeProcessOptions
            {
                EofDelay = TimeSpan.FromSeconds(60),
                ExitDelay = Timeout.InfiniteTimeSpan,
            });
            lock (created) created.Add(process);
            return process;
        });
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(
            ["version"], timeout: TimeSpan.FromMilliseconds(150)));

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.OutputTruncated);
        Assert.True(Single(created).WasKilled);
        Assert.True(Single(created).HasExited);
    }

    [Fact]
    public async Task Caller_cancellation_is_Cancelled_true_and_TimedOut_false()
    {
        var starter = new FakeDockerProcessStarter();
        var created = new List<FakeDockerProcess>();
        starter.Enqueue(_ =>
        {
            var process = new FakeDockerProcess(new FakeProcessOptions
            {
                EofDelay = TimeSpan.FromSeconds(60),
                ExitDelay = Timeout.InfiniteTimeSpan,
            });
            lock (created) created.Add(process);
            return process;
        });
        using var transport = MakeTransport(starter);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var result = await transport.RunAsync(new DockerCliInvocation(["version"]), cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.True(Single(created).WasKilled);
    }

    [Fact]
    public async Task Already_cancelled_caller_token_returns_cancelled_without_starting_a_process()
    {
        var starter = new FakeDockerProcessStarter();
        using var transport = MakeTransport(starter);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await transport.RunAsync(new DockerCliInvocation(["version"]), cts.Token);

        Assert.False(result.Succeeded);
        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Empty(starter.StartedConfigs); // no process was launched
    }

    // ---- startup and I/O failures ------------------------------------------------------------

    [Fact]
    public async Task Startup_failure_is_structured_and_never_labelled_cancelled_or_timed_out()
    {
        var starter = new FakeDockerProcessStarter
        {
            StartException = new InvalidOperationException("executable missing"),
        };
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(["version"]));

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.OutputTruncated);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("failed to start", result.StandardError);
    }

    [Fact]
    public async Task Reader_failure_is_bounded_structured_and_kills_the_process()
    {
        var starter = new FakeDockerProcessStarter();
        var created = new List<FakeDockerProcess>();
        starter.Enqueue(_ =>
        {
            var process = new FakeDockerProcess(new FakeProcessOptions
            {
                StdOut = new string('a', 10_000),
                StdOutChunkSize = 4,
                StdOutThrowsAfterReads = 2,
                EofDelay = TimeSpan.FromMilliseconds(50),
            });
            lock (created) created.Add(process);
            return process;
        });
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(["version"]));

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.False(result.OutputTruncated);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("stdout failure", result.StandardError);
        Assert.True(result.StandardError.Length <= 512, "failure messages are bounded");
        Assert.True(Single(created).WasKilled);
    }

    [Fact]
    public async Task Nonzero_exit_is_reported_without_mislabelling()
    {
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ => new FakeDockerProcess(new FakeProcessOptions
        {
            ExitCode = 7,
            StdErr = "boom",
        }));
        using var transport = MakeTransport(starter);

        var result = await transport.RunAsync(new DockerCliInvocation(["version"]));

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("boom", result.StandardError);
        Assert.False(result.TimedOut);
        Assert.False(result.Cancelled);
    }

    // ---- disposal ------------------------------------------------------------------------------

    [Fact]
    public async Task Disposing_one_transport_does_not_break_another()
    {
        var starter = new FakeDockerProcessStarter();
        starter.DefaultFactory = _ => new FakeDockerProcess(new FakeProcessOptions());
        var first = new DockerCliProcessTransport(starter, _workingDirectory.Root, FakeDockerPath);
        var second = new DockerCliProcessTransport(starter, _otherWorkingDirectory.Root, FakeDockerPath);

        first.Dispose();

        var result = await second.RunAsync(new DockerCliInvocation(["version"]));
        Assert.True(result.Succeeded);
        second.Dispose();
    }

    [Fact]
    public async Task Disposed_transport_refuses_invocations_and_disposal_is_idempotent()
    {
        var starter = new FakeDockerProcessStarter();
        using var transport = MakeTransport(starter);
        transport.Dispose();
        transport.Dispose(); // idempotent

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => transport.RunAsync(new DockerCliInvocation(["version"])));
    }

    [Fact]
    public void Transport_uses_a_per_instance_working_directory_with_its_own_lifetime()
    {
        var starter = new FakeDockerProcessStarter();
        var first = new DockerCliProcessTransport(starter, _workingDirectory.Root, FakeDockerPath);
        var second = new DockerCliProcessTransport(starter, _otherWorkingDirectory.Root, FakeDockerPath);

        first.Dispose();

        // The other instance's trusted working directory still exists after the first disposal.
        Assert.True(Directory.Exists(_otherWorkingDirectory.Root));
        second.Dispose();
        Assert.False(Directory.Exists(_otherWorkingDirectory.Root));
        Assert.False(Directory.Exists(_workingDirectory.Root));
    }
}

/// <summary>
/// Process-lifetime and I/O-failure termination tests: the production transport must dispose
/// the process exactly once on every path, and a reader/writer failure must cancel, kill and
/// terminate the invocation promptly instead of waiting for the normal timeout.
/// </summary>
public class DockerCliProcessTransportLifetimeTests : IDisposable
{
    private readonly TempDir _workingDirectory = new();
    private const string FakeDockerPath = "/usr/bin/tenninety-fake-docker";

    public void Dispose() => _workingDirectory.Dispose();

    private string WorkingDirectoryRoot => _workingDirectory.Root;

    /// <summary>Builds a transport whose host lookup returns the given values (null default),
    /// so tests control the "host environment" completely without touching the real one.</summary>
    private DockerCliProcessTransport MakeTransport(
        FakeDockerProcessStarter starter,
        Func<string, string?>? lookup = null) =>
        new(starter, _workingDirectory.Root, FakeDockerPath,
            useIsolatedClientEnvironment: true, hostEnvironmentLookup: lookup);

    private static FakeDockerProcess Single(List<FakeDockerProcess> created) => created.Single();

    private static FakeDockerProcessStarter StarterWith(
        FakeProcessOptions options, List<FakeDockerProcess> created)
    {
        var starter = new FakeDockerProcessStarter();
        starter.Enqueue(_ =>
        {
            var process = new FakeDockerProcess(options);
            lock (created) created.Add(process);
            return process;
        });
        return starter;
    }

    [Fact]
    public async Task Process_is_disposed_exactly_once_after_normal_completion()
    {
        var created = new List<FakeDockerProcess>();
        var starter = StarterWith(new FakeProcessOptions { StdOut = "ok" }, created);
        using var transport = MakeTransport(starter);
        var result = await transport.RunAsync(new DockerCliInvocation(["version"]));
        Assert.True(result.Succeeded);
        Assert.Equal(1, Single(created).DisposeCount);
    }

    [Fact]
    public async Task Process_is_disposed_exactly_once_after_nonzero_exit()
    {
        var created = new List<FakeDockerProcess>();
        var starter = StarterWith(new FakeProcessOptions { ExitCode = 3 }, created);
        using var transport = MakeTransport(starter);
        var result = await transport.RunAsync(new DockerCliInvocation(["version"]));
        Assert.False(result.Succeeded);
        Assert.Equal(1, Single(created).DisposeCount);
    }

    [Fact]
    public async Task Process_is_disposed_exactly_once_after_timeout()
    {
        var created = new List<FakeDockerProcess>();
        var starter = StarterWith(new FakeProcessOptions
        {
            EofDelay = TimeSpan.FromSeconds(60),
            ExitDelay = Timeout.InfiniteTimeSpan,
        }, created);
        using var transport = MakeTransport(starter);
        var result = await transport.RunAsync(new DockerCliInvocation(
            ["version"], timeout: TimeSpan.FromMilliseconds(150)));
        Assert.True(result.TimedOut);
        Assert.Equal(1, Single(created).DisposeCount);
    }

    [Fact]
    public async Task Process_is_disposed_exactly_once_after_cancellation()
    {
        var created = new List<FakeDockerProcess>();
        var starter = StarterWith(new FakeProcessOptions
        {
            EofDelay = TimeSpan.FromSeconds(60),
            ExitDelay = Timeout.InfiniteTimeSpan,
        }, created);
        using var transport = MakeTransport(starter);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var result = await transport.RunAsync(new DockerCliInvocation(["version"]), cts.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(1, Single(created).DisposeCount);
    }

    [Fact]
    public async Task Process_is_disposed_exactly_once_after_truncation()
    {
        var created = new List<FakeDockerProcess>();
        var starter = StarterWith(new FakeProcessOptions
        {
            StdOut = new string('x', 500),
            StdOutChunkSize = 64,
            EofDelay = TimeSpan.FromSeconds(60),
        }, created);
        using var transport = MakeTransport(starter);
        var result = await transport.RunAsync(new DockerCliInvocation(
            ["version"], maxOutputBytes: 100));
        Assert.True(result.OutputTruncated);
        Assert.Equal(1, Single(created).DisposeCount);
    }

    [Fact]
    public async Task Process_is_disposed_exactly_once_after_startup_followed_by_io_failure()
    {
        var created = new List<FakeDockerProcess>();
        var starter = StarterWith(new FakeProcessOptions
        {
            StdOut = new string('a', 10_000),
            StdOutChunkSize = 4,
            StdOutThrowsAfterReads = 2,
            EofDelay = TimeSpan.FromMilliseconds(50),
        }, created);
        using var transport = MakeTransport(starter);
        var result = await transport.RunAsync(new DockerCliInvocation(["version"]));
        Assert.False(result.Succeeded);
        Assert.Contains("stdout failure", result.StandardError);
        Assert.False(result.Cancelled, "an I/O failure must not be labelled cancelled");
        Assert.False(result.TimedOut);
        Assert.Equal(1, Single(created).DisposeCount);
    }

    [Fact]
    public async Task Reader_failure_terminates_promptly_instead_of_waiting_for_the_timeout()
    {
        // The stream would not reach EOF for 30 seconds, and the invocation timeout is 10 s;
        // the reader failure must cancel, kill and finish far earlier than either.
        var created = new List<FakeDockerProcess>();
        var starter = StarterWith(new FakeProcessOptions
        {
            StdOut = new string('a', 10_000),
            StdOutChunkSize = 4,
            StdOutThrowsAfterReads = 2,
            EofDelay = TimeSpan.FromSeconds(30),
        }, created);
        using var transport = MakeTransport(starter);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await transport.RunAsync(new DockerCliInvocation(
            ["version"], timeout: TimeSpan.FromSeconds(10)));
        sw.Stop();

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut, "the I/O failure — not the timeout — must end the invocation");
        Assert.False(result.Cancelled);
        Assert.Contains("stdout failure", result.StandardError);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5),
            $"reader failure must terminate promptly, took {sw.Elapsed}");
        Assert.True(Single(created).WasKilled);
        Assert.Equal(1, Single(created).DisposeCount);
    }
}

/// <summary>
/// Startup-failure classification: after the pre-start cancellation check passes, a starter
/// exception is a STARTUP failure — never caller cancellation — even when the starter cancels
/// the caller token immediately before throwing.
/// </summary>
public class DockerCliProcessTransportStartupClassificationTests : IDisposable
{
    private readonly TempDir _workingDirectory = new();
    private const string FakeDockerPath = "/usr/bin/tenninety-fake-docker";

    public void Dispose() => _workingDirectory.Dispose();

    private sealed class CancellingThenThrowingStarter : IDockerProcessStarter
    {
        private readonly CancellationTokenSource _callerCts;
        public CancellingThenThrowingStarter(CancellationTokenSource callerCts) => _callerCts = callerCts;

        public IDockerProcess Start(DockerCliProcessConfig config)
        {
            // Simulate the race: the caller token is cancelled during the start attempt.
            _callerCts.Cancel();
            throw new InvalidOperationException("executable missing");
        }
    }

    [Fact]
    public async Task Startup_failure_during_cancellation_is_still_classified_as_startup_failure()
    {
        using var cts = new CancellationTokenSource();
        var starter = new CancellingThenThrowingStarter(cts);
        var transport = new DockerCliProcessTransport(starter, _workingDirectory.Root, FakeDockerPath);
        try
        {
            var result = await transport.RunAsync(new DockerCliInvocation(["version"]), cts.Token);

            Assert.False(result.Succeeded);
            Assert.False(result.TimedOut);
            Assert.False(result.Cancelled, "a genuine startup failure must never be marked cancelled");
            Assert.False(result.OutputTruncated);
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("failed to start", result.StandardError);
        }
        finally
        {
            transport.Dispose();
        }
    }

    [Fact]
    public async Task Already_cancelled_token_before_startup_returns_cancelled_without_starting()
    {
        var starter = new FakeDockerProcessStarter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var transport = new DockerCliProcessTransport(starter, _workingDirectory.Root, FakeDockerPath);
        try
        {
            var result = await transport.RunAsync(new DockerCliInvocation(["version"]), cts.Token);
            Assert.True(result.Cancelled);
            Assert.False(result.TimedOut);
            Assert.Empty(starter.StartedConfigs);
        }
        finally
        {
            transport.Dispose();
        }
    }
}
