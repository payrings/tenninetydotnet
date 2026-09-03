using System.Text;
using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Shape-aware, stateful fake Docker transport for preflight tests. Every invocation is
/// routed by its argument shape (version / info / image inspect / network inspect / create /
/// start / inspect / exec / stop / kill / rm) against a per-probe lifecycle state machine
/// (Created → Started → Stopped/Killed → Removed). Kill fallbacks, start failures, rm
/// failures and absence confirmations are all handled by state — never by handler positions.
/// Scenario knobs let each test inject exactly one defect into the real production call
/// order.
/// </summary>
public sealed class PreflightFakeTransport : IDockerCliTransport
{
    public enum RmMode { Ok, FailBusy, ClaimAbsent }
    private enum Phase { Removed, Created, Started, Stopped, Killed }

    private readonly SandboxConfig _cfg;
    private Phase _phase = Phase.Removed;
    private int _inspectCount;
    private bool _postStopRunningConsumed;
    private bool _absenceContradictionUsed;

    public readonly List<DockerCliInvocation> Invocations = new();
    public readonly List<DockerCliInvocation> Creates = new();
    public readonly List<DockerCliInvocation> NetworkInspects = new();
    public int CreatedProbes;
    public int ProbesRemovedAndProven;

    // ---- scripted daemon facts -----------------------------------------------------
    public string? InfoJsonOverride;
    public DockerCliResult? VersionResult;
    public readonly Dictionary<string, string> ImageInspectOverride = new();
    public readonly Dictionary<string, string> ImageUserOverride = new();
    public readonly HashSet<string> MissingNetworks = new(StringComparer.Ordinal);
    public string? NetworkInspectNameOverride;
    public bool NetworkInspectTimeout;

    public static readonly string CoderImageId = "sha256:" + new string('a', 64);
    public static readonly string ReviewerImageId = "sha256:" + new string('b', 64);
    public static readonly string TesterImageId = "sha256:" + new string('c', 64);
    public static readonly string NetworkIdFixed = new('d', 64);
    public static readonly string ProbeContainerIdFixed = new('1', 64);

    // ---- scenario overrides ----------------------------------------------------------
    public Func<string, string>? DetailedInspectMutator;
    public DockerCliResult? StartResult;
    public int StartFailureCount;
    public RmMode Rm = RmMode.Ok;
    public bool AbsenceContradiction;
    public bool PostStopInspectRunning;
    public DockerCliResult? WorkspaceWriteResult;
    public DockerCliResult? RootWriteResult;
    public DockerCliResult? TmpWriteResult;
    public DockerCliResult? HomeWriteResult;

    /// <summary>Overrides the `docker create` stdout (e.g. a malformed container id) so tests
    /// can exercise a create that was ATTEMPTED but produced no usable identity.</summary>
    public string? CreateResultOverride;

    /// <summary>When true, the workspace-write probe actually writes a sentinel file into the
    /// probe's mounted host workspace (simulating the real container write), so tests can
    /// assert that the probe workspace and its sentinel content survive or vanish.</summary>
    public bool TouchHostWorkspaceOnWriteProbe;

    public PreflightFakeTransport(SandboxConfig config) => _cfg = config;

    public Task<DockerCliResult> RunAsync(DockerCliInvocation invocation, CancellationToken ct = default)
    {
        Invocations.Add(invocation);
        var op = invocation.Arguments[0];
        return Task.FromResult(op switch
        {
            "version" => HandleVersion(),
            "info" => Ok(InfoJsonOverride ?? DockerSandboxPreflightTests.InfoJson()),
            "image" => HandleImageInspect(invocation),
            "network" => HandleNetworkInspect(invocation),
            "create" => HandleCreate(invocation),
            "start" => HandleStart(),
            "inspect" => HandleInspect(invocation),
            "exec" => HandleExec(invocation),
            "stop" => HandleStop(),
            "kill" => HandleKill(),
            "rm" => HandleRm(),
            _ => throw new InvalidOperationException(
                "unexpected docker call in the fake transport: '" + op + "'."),
        });
    }

    private static DockerCliResult Ok(string stdout = "") =>
        new(0, stdout, "", TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private static DockerCliResult Err(string stderr) =>
        new(1, "", stderr, TimedOut: false, Cancelled: false,
            OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));

    private DockerCliResult HandleVersion() =>
        VersionResult ?? Ok("{\"Client\":{\"Version\":\"1.0\"}," +
            "\"Server\":{\"Version\":\"29.0\",\"Os\":\"linux\",\"Arch\":\"amd64\"}}");

    private DockerCliResult HandleImageInspect(DockerCliInvocation invocation)
    {
        var reference = invocation.Arguments[^1];
        if (ImageInspectOverride.TryGetValue(reference, out var json))
            return Ok(json);
        var imageId = ImageIdFor(reference);
        var user = ImageUserOverride.TryGetValue(imageId, out var u) ? u : "1000:1000";
        return Ok("[{\"Id\":\"" + imageId + "\",\"RepoDigests\":[],\"Config\":{\"User\":\"" +
                  user + "\",\"Entrypoint\":[]}}]");
    }

    private string ImageIdFor(string reference) => reference switch
    {
        var r when r == _cfg.Roles.Coder.Image => CoderImageId,
        var r when r == _cfg.Roles.Reviewer.Image => ReviewerImageId,
        var r when r == _cfg.Roles.Tester.Image => TesterImageId,
        _ => CoderImageId,
    };

    private DockerCliResult HandleNetworkInspect(DockerCliInvocation invocation)
    {
        var name = invocation.Arguments[^1];
        NetworkInspects.Add(invocation);
        if (NetworkInspectTimeout)
            return new DockerCliResult(-1, "", "", TimedOut: true, Cancelled: false,
                OutputTruncated: false, Duration: TimeSpan.FromMilliseconds(1));
        if (MissingNetworks.Contains(name))
            return Err("Error: No such network: " + name);
        var reported = NetworkInspectNameOverride ?? name;
        return Ok($"[{{\"Name\":\"{reported}\",\"Id\":\"{NetworkIdFixed}\",\"Driver\":\"bridge\"}}]");
    }

    private DockerCliResult HandleCreate(DockerCliInvocation invocation)
    {
        Creates.Add(invocation);
        CreatedProbes++;
        Capture(invocation);
        _phase = Phase.Created;
        _inspectCount = 0;
        _postStopRunningConsumed = false;
        return CreateResultOverride is { } overridden ? Ok(overridden) : Ok(ProbeContainerId());
    }

    private DockerCliResult HandleStart()
    {
        if (StartResult is { } start && StartFailureCount > 0)
        {
            StartFailureCount--;
            return start; // phase stays Created: the start failed
        }
        _phase = Phase.Started;
        return Ok("");
    }

    private DockerCliResult HandleInspect(DockerCliInvocation invocation)
    {
        var args = invocation.Arguments.ToList();
        var requestedId = args[^1];
        if (!string.Equals(requestedId, ProbeContainerId(), StringComparison.Ordinal))
            return Ok($"[{{\"Id\":\"garbage\",\"Image\":\"sha256:x\",\"State\":{{\"Running\":true}}}}]");

        if (_phase == Phase.Removed)
        {
            // The absence confirmation after rm — or an inspect of an unknown container.
            if (AbsenceContradiction && !_absenceContradictionUsed)
            {
                _absenceContradictionUsed = true;
                return Ok("[" + StateJsonWrapper(running: false) + "]");
            }
            return Err("Error: No such object: " + requestedId);
        }

        if (_phase is Phase.Stopped or Phase.Killed or Phase.Created)
        {
            var running = _phase == Phase.Created
                ? false
                : PostStopInspectRunning && !_postStopRunningConsumed;
            if (running) _postStopRunningConsumed = true;
            return Ok("[" + StateJsonWrapper(running) + "]");
        }

        // Started: first inspect proves running, second delivers the detailed fixture.
        _inspectCount++;
        if (_inspectCount == 1)
            return Ok("[" + StateJsonWrapper(running: true) + "]");

        var imageId = _capturedImageId.Length == 0 ? CoderImageId : _capturedImageId;
        var user = _capturedUser.Length == 0 ? "1000:1000" : _capturedUser;
        var source = _capturedSource;
        var network = _capturedNetwork.Length == 0 ? "none" : _capturedNetwork;
        var json = DockerSandboxPreflightTests.BuildDetailedInspect(
            ProbeContainerId(), imageId, source, network, user,
            nanoCpus: (long)(_capturedCpus * 1_000_000_000L),
            memoryBytes: (long)_capturedMemoryMb * 1024 * 1024,
            pids: _capturedPids);
        return Ok(DetailedInspectMutator?.Invoke(json) ?? json);
    }

    private string StateJsonWrapper(bool running) =>
        "{\"Id\":\"" + ProbeContainerId() + "\",\"Image\":\"" + _capturedImageId + "\"," +
        DockerSandboxPreflightTests.StateJson(running) + ",\"Config\":{},\"HostConfig\":{}}";

    private DockerCliResult HandleExec(DockerCliInvocation invocation)
    {
        var joined = string.Join(" ", invocation.Arguments);
        if (joined.Contains("/workspace/.tenninety-preflight-write", StringComparison.Ordinal))
        {
            if (TouchHostWorkspaceOnWriteProbe && WorkspaceWriteResult is null &&
                _capturedSource.Length > 0)
            {
                // Simulate the REAL container side effect: the probe writes into the
                // workspace that is bind-mounted from the captured host source.
                File.WriteAllText(
                    Path.Combine(_capturedSource, ".tenninety-preflight-write"), "probe");
            }
            return WorkspaceWriteResult ?? Ok("");
        }
        if (joined.Contains("/.tenninety-preflight-ro-probe", StringComparison.Ordinal))
            return RootWriteResult ?? Err("Read-only file system");
        if (joined.Contains("/tmp/.tenninety-preflight-tmp", StringComparison.Ordinal))
            return TmpWriteResult ?? Ok("");
        if (joined.Contains(".tenninety-preflight-home", StringComparison.Ordinal))
            return HomeWriteResult ?? Ok("");
        return Ok("");
    }

    private DockerCliResult HandleStop()
    {
        _phase = Phase.Stopped;
        return Ok("");
    }

    private DockerCliResult HandleKill()
    {
        _phase = Phase.Killed;
        return Ok("");
    }

    private DockerCliResult HandleRm()
    {
        switch (Rm)
        {
            case RmMode.FailBusy:
                return Err("Error response from daemon: device or resource busy");
            case RmMode.ClaimAbsent:
                _phase = Phase.Removed;
                ProbesRemovedAndProven++;
                return Err("Error: No such container: " + ProbeContainerId());
            default:
                _phase = Phase.Removed;
                ProbesRemovedAndProven++;
                return Ok(ProbeContainerId());
        }
    }

    // ---- captured per-probe create facts -------------------------------------------

    private string _capturedSource = "";
    private string _capturedNetwork = "";
    private string _capturedImageId = "";
    private string _capturedUser = "1000:1000";
    private double _capturedCpus = 1.0;
    private int _capturedMemoryMb = 256;
    private int _capturedPids = 64;

    private string ProbeContainerId() => "1111111111111111111111111111111111111111111111111111111111111111";

    private void Capture(DockerCliInvocation invocation)
    {
        var args = invocation.Arguments.ToList();
        var mountIndex = args.IndexOf("--mount");
        if (mountIndex >= 0)
        {
            var mount = args[mountIndex + 1];
            _capturedSource = mount.Split("source=")[1].Split(',')[0];
        }
        var networkIndex = args.IndexOf("--network");
        if (networkIndex >= 0) _capturedNetwork = args[networkIndex + 1];
        var userIndex = args.IndexOf("--user");
        if (userIndex >= 0) _capturedUser = args[userIndex + 1];
        var cpusIndex = args.IndexOf("--cpus");
        if (cpusIndex >= 0 &&
            double.TryParse(args[cpusIndex + 1],
                System.Globalization.CultureInfo.InvariantCulture, out var cpus))
            _capturedCpus = cpus;
        var memoryIndex = args.IndexOf("--memory");
        if (memoryIndex >= 0 && args[memoryIndex + 1].EndsWith("m"))
            _capturedMemoryMb = int.Parse(args[memoryIndex + 1][..^1],
                System.Globalization.CultureInfo.InvariantCulture);
        var pidsIndex = args.IndexOf("--pids-limit");
        if (pidsIndex >= 0) _capturedPids = int.Parse(args[pidsIndex + 1],
            System.Globalization.CultureInfo.InvariantCulture);
        // Image ID: the create vector ends with [imageId, "sleep", "infinity"].
        _capturedImageId = args[^3];
    }

    // ---- detailed-inspect fixture generation ----------------------------------------

    private string BuildDetailedInspect(string imageId, string user, string source, string network)
    {
        // Reuse the shared fixture builder from the test class via a static helper.
        return DockerSandboxPreflightTests.BuildDetailedInspect(
            ProbeContainerId(), imageId, source, network, user);
    }

}
