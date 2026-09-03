using System.Text.Json;

namespace Tenninety.Execution.Sandbox;

// ---- strict JSON gate -------------------------------------------------------

/// <summary>
/// Strict JSON pre-parse gate: System.Text.Json silently accepts duplicate property names
/// (last one wins), so every Docker output that feeds a security decision is first walked
/// with a bounded <see cref="Utf8JsonReader"/> pass that rejects ANY duplicate field. Real
/// Docker JSON never contains duplicates; anything that does is treated as hostile.
/// </summary>
internal static class StrictJson
{
    private const int MaxDepth = 64;
    private const int MaxPropertyNameLength = 256;

    public static void EnsureNoDuplicateFields(byte[] json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var stack = new Stack<HashSet<string>>();
        var depth = 0;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    if (++depth > MaxDepth)
                        throw new InvalidOperationException("docker JSON nesting depth exceeded the bound.");
                    stack.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    stack.Pop();
                    depth--;
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString() ?? "";
                    if (name.Length > MaxPropertyNameLength)
                        throw new InvalidOperationException("docker JSON property name exceeds the bound.");
                    if (!stack.Peek().Add(name))
                        throw new InvalidOperationException(
                            $"docker JSON contains a duplicate field '{Bounded(name)}'; treating the output as hostile.");
                    break;
            }
        }
    }

    internal static string Bounded(string value) =>
        value.Length <= 128 ? value : value[..128] + "…";
}

// ---- shape validation helpers ------------------------------------------------

/// <summary>Strict shape validation for Docker identifiers before they are reused as
/// arguments or exposed anywhere.</summary>
internal static class DockerValidation
{
    public const int HexLength = 64;

    public static bool IsHex64(string? value) =>
        value is { Length: HexLength } &&
        value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Exact local image ID: `sha256:` plus 64 lowercase hex characters.</summary>
    public static bool IsSha256ImageId(string? value) =>
        value is { Length: 71 } &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        IsHex64(value[7..]);

    /// <summary>Full container ID: 64 lowercase hex characters (never blank, multiline,
    /// option-like, or ambiguous).</summary>
    public static bool IsContainerId(string? value) => IsHex64(value);

    public static void RequireImageId(string value, string what)
    {
        if (!IsSha256ImageId(value))
            throw new InvalidOperationException(
                $"{what} is not a well-formed exact image id (sha256:<64 lowercase hex>): " +
                $"'{StrictJson.Bounded(value)}'.");
    }

    public static void RequireContainerId(string value, string what)
    {
        if (!IsContainerId(value))
            throw new InvalidOperationException(
                $"{what} is not a well-formed full container id (64 lowercase hex characters): " +
                $"'{StrictJson.Bounded(value)}'.");
    }

    /// <summary>A create/exec network argument: either the offline policy target `none` or a
    /// validated, non-reserved, well-formed Docker network name. Host, default bridge, and
    /// every other reserved name are unreachable.</summary>
    public static void RequireNetworkArg(string name, string what)
    {
        if (name == "none") return;
        if (!Tenninety.Core.Models.SandboxConfig.IsValidDockerNetworkName(name))
            throw new InvalidOperationException(
                $"{what} '{StrictJson.Bounded(name)}' is not a permitted Docker network name: " +
                "reserved networks (host, bridge, none, default), malformed names, and names " +
                "with whitespace or control characters are rejected. Host networking is never permitted.");
    }

    /// <summary>A structured --mount bind source must be an absolute printable-ASCII POSIX
    /// path that cannot inject --mount grammar delimiters. Commas, double quotes, control
    /// characters and NUL fail closed (escaping is not implemented, so injection is refused).</summary>
    public static void RequireMountSource(string source, string what)
    {
        if (string.IsNullOrWhiteSpace(source) || !source.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException($"{what} must be an absolute POSIX path.");
        if (source.Contains(',') || source.Contains('"') || source.Contains('\'') ||
            source.Contains('\\') || source.Contains(':') || source.Contains('\0') ||
            source.Any(char.IsControl))
            throw new InvalidOperationException(
                $"{what} contains characters that cannot be represented safely in Docker's " +
                "structured --mount grammar (comma, quote, backslash, colon, control character " +
                "or NUL); the mount is refused instead of escaped.");
    }

    public static void RequireContainerName(string name, string what)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            !char.IsAsciiLetterOrDigit(name[0]) ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new InvalidOperationException(
                $"{what} is not a well-formed container name: '{StrictJson.Bounded(name)}'.");
    }
}

// ---- daemon info records ---------------------------------------------------

/// <summary>Single-call `docker info` result: daemon facts plus the hostile-output marker
/// for SecurityOptions (a malformed shape must never count as LSM evidence).</summary>
public sealed record DockerDaemonFacts(DockerDaemonInfo Info, bool SecurityOptionsMalformed);

/// <summary>Parsed Docker version output (daemon connectivity).</summary>
public sealed record DockerDaemonInfo(
    string ServerVersion,
    string OsType,
    string Architecture,
    bool Rootless,
    string CgroupVersion,
    string CgroupDriver,
    IReadOnlySet<string> SecurityOptions)
{
    public bool HasAppArmor => SecurityOptions.Any(o =>
        o.StartsWith("name=apparmor", StringComparison.OrdinalIgnoreCase));
    public bool HasSeccomp => SecurityOptions.Any(o =>
        o.StartsWith("name=seccomp", StringComparison.OrdinalIgnoreCase));
    public bool HasSelinux => SecurityOptions.Any(o =>
        o.StartsWith("name=selinux", StringComparison.OrdinalIgnoreCase));

    /// <summary>Cgroup enforcement is reliable only when the driver is systemd or cgroupfs
    /// (the two Linux cgroup drivers Docker supports) and the version is known. Unknown or
    /// missing information is a hard fail for live execution.</summary>
    public bool CgroupEnforcementReliable =>
        CgroupVersion is "1" or "2" &&
        CgroupDriver is "systemd" or "cgroupfs";
}

// ---- parsed image info -----------------------------------------------------

/// <summary>Exact image identity after local inspection: the exact local image ID, ALL
/// repository digests (never only the first), the configured user, and the configured
/// entrypoint (a non-empty entrypoint breaks the fixed waiting command and fails closed).</summary>
public sealed record DockerImageInfo(
    string ImageId,
    IReadOnlyList<string> RepoDigests,
    string ConfiguredUser,
    IReadOnlyList<string> ConfigEntrypoint);

/// <summary>Verified non-root container identity (numeric uid, optional numeric gid).</summary>
public sealed record ContainerIdentity(int Uid, int? Gid)
{
    public bool IsRoot => Uid == 0;

    public static ContainerIdentity Parse(string user)
    {
        if (string.IsNullOrWhiteSpace(user))
            throw new InvalidOperationException("image configured user is missing or blank.");
        var parts = user.Split(':');
        if (parts.Length is < 1 or > 2)
            throw new InvalidOperationException(
                $"image configured user '{StrictJson.Bounded(user)}' is malformed: expected uid[:gid].");
        if (parts[0].Length == 0 || !parts[0].All(char.IsAsciiDigit) ||
            !int.TryParse(parts[0], out var uid))
            throw new InvalidOperationException(
                $"image configured user '{StrictJson.Bounded(user)}' has a non-numeric uid: sandbox " +
                "containers must run as an explicit numeric non-root identity (e.g. USER 1000:1000).");
        int? gid = null;
        if (parts.Length == 2)
        {
            if (parts[1].Length == 0 || !parts[1].All(char.IsAsciiDigit) ||
                !int.TryParse(parts[1], out var gidVal))
                throw new InvalidOperationException(
                    $"image configured user '{StrictJson.Bounded(user)}' has a non-numeric gid: " +
                    "sandbox containers must run as an explicit numeric identity.");
            gid = gidVal;
        }
        if (uid == 0)
            throw new InvalidOperationException(
                "image configured user is root (uid=0). Sandbox containers must run as a non-root " +
                "numeric identity; rootless mode must also use the least-privileged workable identity.");
        return new ContainerIdentity(uid, gid);
    }

    public string ToUserFlag() => Gid is { } g ? $"{Uid}:{g}" : Uid.ToString();
}

// ---- container state records -----------------------------------------------

/// <summary>Parsed `docker inspect` state summary for one container.</summary>
public sealed record DockerContainerState(
    string ContainerId,
    string ImageId,
    bool Running,
    bool Paused,
    bool OomKilled,
    int ExitCode)
{
    public static DockerContainerState FromJson(byte[] inspectJson)
    {
        try
        {
            StrictJson.EnsureNoDuplicateFields(inspectJson);
            using var doc = JsonDocument.Parse(inspectJson);
            var root = DockerJsonParsing.FirstArrayElement(doc);
            var id = DockerJsonParsing.GetRequiredString(root, "Id");
            DockerValidation.RequireContainerId(id, "container inspect Id");
            var imageId = DockerJsonParsing.GetRequiredString(root, "Image");
            DockerValidation.RequireImageId(imageId, "container inspect Image");
            var state = root.GetProperty("State");
            return new DockerContainerState(
                ContainerId: id,
                ImageId: imageId,
                Running: state.GetProperty("Running").GetBoolean(),
                Paused: state.TryGetProperty("Paused", out var p) && p.GetBoolean(),
                OomKilled: state.TryGetProperty("OOMKilled", out var o) && o.GetBoolean(),
                ExitCode: state.TryGetProperty("ExitCode", out var e) ? e.GetInt32() : -1);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "docker container inspect returned malformed or incomplete JSON.", ex);
        }
    }
}

/// <summary>One mount from HostConfig.Mounts (Target/BindOptions representation).</summary>
public sealed record DockerMountInfo(string Type, string Source, string Target, string? Propagation);

/// <summary>One mount from the TOP-LEVEL `docker inspect` Mounts array — the effective
/// runtime view, including writability (RW) and propagation.</summary>
public sealed record DockerEffectiveMount(
    string Type,
    string Source,
    string Destination,
    bool? Rw,
    string? Propagation,
    string? Mode);

public sealed record DockerUlimit(string Name, long Soft, long Hard);

/// <summary>
/// Full typed view of a realistic `docker inspect` object used by preflight to verify the
/// EFFECTIVE hardening settings rather than trusting successful create. Security-relevant
/// fields are REQUIRED with strict types: a missing, malformed or wrongly-typed field fails
/// closed instead of silently defaulting. HostConfig.Mounts (Target/BindOptions) and the
/// top-level Mounts (Destination/RW/Propagation) are both parsed so the two representations
/// can be cross-checked for contradiction.
/// </summary>
public sealed record DockerContainerDetailed(
    string ContainerId,
    string ImageId,
    bool Running,
    bool OomKilled,
    string? User,
    string? WorkingDir,
    long NanoCpus,
    long MemoryBytes,
    long? PidsLimit,
    bool ReadonlyRootfs,
    IReadOnlyList<string> CapDrop,
    IReadOnlyList<string> CapAdd,
    bool Privileged,
    IReadOnlyList<string> SecurityOpt,
    string NetworkMode,
    string PidMode,
    string IpcMode,
    IReadOnlyList<DockerEffectiveMount> Mounts,
    IReadOnlyList<DockerMountInfo> HostMounts,
    IReadOnlyDictionary<string, string> Tmpfs,
    IReadOnlyList<DockerUlimit> Ulimits,
    int PortBindingCount,
    int DeviceCount)
{
    public static DockerContainerDetailed FromJson(byte[] inspectJson)
    {
        try
        {
            StrictJson.EnsureNoDuplicateFields(inspectJson);
            using var doc = JsonDocument.Parse(inspectJson);
            var root = DockerJsonParsing.FirstArrayElement(doc);
            var id = DockerJsonParsing.GetRequiredString(root, "Id");
            DockerValidation.RequireContainerId(id, "container inspect Id");
            var imageId = DockerJsonParsing.GetRequiredString(root, "Image");
            DockerValidation.RequireImageId(imageId, "container inspect Image");

            var state = DockerJsonParsing.GetRequiredObject(root, "State");
            var config = DockerJsonParsing.GetRequiredObject(root, "Config");
            var host = DockerJsonParsing.GetRequiredObject(root, "HostConfig");

            // ---- top-level effective mounts (Destination/RW/Propagation) ----
            var mounts = new List<DockerEffectiveMount>();
            if (!root.TryGetProperty("Mounts", out var mountsEl) ||
                mountsEl.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(
                    "container inspect is missing the top-level Mounts array; the effective " +
                    "mount state cannot be verified.");
            foreach (var m in mountsEl.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException(
                        "container inspect Mounts contains a non-object entry.");
                mounts.Add(new DockerEffectiveMount(
                    Type: DockerJsonParsing.GetRequiredString(m, "Type"),
                    Source: m.TryGetProperty("Source", out var ms) && ms.ValueKind == JsonValueKind.String
                        ? ms.GetString() ?? "" : "",
                    Destination: m.TryGetProperty("Destination", out var md) && md.ValueKind == JsonValueKind.String
                        ? md.GetString() ?? "" : "",
                    Rw: m.TryGetProperty("RW", out var rw) && rw.ValueKind == JsonValueKind.True
                        ? true : m.TryGetProperty("RW", out var rwf) && rwf.ValueKind == JsonValueKind.False
                        ? false : null,
                    Propagation: m.TryGetProperty("Propagation", out var pr) && pr.ValueKind == JsonValueKind.String
                        ? pr.GetString() : null,
                    Mode: m.TryGetProperty("Mode", out var mo) && mo.ValueKind == JsonValueKind.String
                        ? mo.GetString() : null));
            }

            // ---- HostConfig.Mounts (Target/BindOptions.Propagation) ----
            // Real Docker legitimately omits or nulls this field; absence and null are valid
            // empty representations. A present array requires every entry to be a fully and
            // strictly typed object: Type, Source and Target must be present non-blank JSON
            // strings, and a bind entry must carry a usable BindOptions.Propagation proof.
            // A missing, null, blank or wrongly-typed member fails closed — it is never
            // silently rewritten into "" or null, which would let a hostile mount evade the
            // HostConfig bind cross-check.
            var hostMounts = new List<DockerMountInfo>();
            if (host.TryGetProperty("Mounts", out var hostMountsEl))
            {
                if (hostMountsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in hostMountsEl.EnumerateArray())
                    {
                        if (m.ValueKind != JsonValueKind.Object)
                            throw new InvalidOperationException(
                                "container inspect HostConfig.Mounts contains a non-object entry.");
                        hostMounts.Add(ParseHostMountEntry(m));
                    }
                }
                else if (hostMountsEl.ValueKind != JsonValueKind.Null &&
                         hostMountsEl.ValueKind != JsonValueKind.Undefined)
                {
                    throw new InvalidOperationException(
                        "container inspect HostConfig.Mounts is neither an array nor null; " +
                        "treating the output as hostile.");
                }
            }

            // ---- tmpfs (exact options are policy-verified by the caller) ----
            // Real Docker may represent an empty tmpfs map as null; strict nullable parsing
            // still fails closed on any wrong type, and the verifier still requires the two
            // exact bounded entries.
            var tmpfs = DockerJsonParsing.GetRequiredNullableStringObject(
                host, "Tmpfs", missingIsEmpty: true);

            // ---- ulimits (strict typed members; present array, null, or missing fails closed) ----
            if (!host.TryGetProperty("Ulimits", out var ulEl) ||
                (ulEl.ValueKind != JsonValueKind.Array && ulEl.ValueKind != JsonValueKind.Null))
                throw new InvalidOperationException(
                    "container inspect HostConfig.Ulimits is missing or is neither an array " +
                    "nor null; the effective open-file-descriptor state cannot be verified.");
            var ulimits = new List<DockerUlimit>();
            if (ulEl.ValueKind == JsonValueKind.Array)
                foreach (var u in ulEl.EnumerateArray())
                {
                    if (u.ValueKind != JsonValueKind.Object)
                        throw new InvalidOperationException(
                            "container inspect Ulimits contains a non-object entry.");
                    ulimits.Add(new DockerUlimit(
                        Name: DockerJsonParsing.GetRequiredString(u, "Name"),
                        Soft: DockerJsonParsing.GetRequiredInt64(u, "Soft"),
                        Hard: DockerJsonParsing.GetRequiredInt64(u, "Hard")));
                }

            var portBindingCount = DockerJsonParsing.GetRequiredNullableObjectCount(host, "PortBindings");
            var deviceCount = DockerJsonParsing.GetRequiredNullableArrayCount(host, "Devices");

            return new DockerContainerDetailed(
                ContainerId: id,
                ImageId: imageId,
                Running: DockerJsonParsing.GetRequiredBool(state, "Running"),
                OomKilled: state.TryGetProperty("OOMKilled", out var oom) && oom.GetBoolean(),
                User: config.TryGetProperty("User", out var user) && user.ValueKind == JsonValueKind.String
                    ? user.GetString() : null,
                WorkingDir: config.TryGetProperty("WorkingDir", out var wd) && wd.ValueKind == JsonValueKind.String
                    ? wd.GetString() : null,
                NanoCpus: DockerJsonParsing.GetRequiredInt64(host, "NanoCpus"),
                MemoryBytes: DockerJsonParsing.GetRequiredInt64(host, "Memory"),
                PidsLimit: host.TryGetProperty("PidsLimit", out var pl) &&
                    pl.ValueKind == JsonValueKind.Number ? pl.GetInt64() : null,
                ReadonlyRootfs: DockerJsonParsing.GetRequiredBool(host, "ReadonlyRootfs"),
                CapDrop: DockerJsonParsing.GetRequiredNullableStringArray(host, "CapDrop"),
                CapAdd: DockerJsonParsing.GetRequiredNullableStringArray(host, "CapAdd"),
                Privileged: DockerJsonParsing.GetRequiredBool(host, "Privileged"),
                SecurityOpt: DockerJsonParsing.GetRequiredNullableStringArray(host, "SecurityOpt"),
                NetworkMode: DockerJsonParsing.GetRequiredStringAllowBlank(host, "NetworkMode"),
                PidMode: DockerJsonParsing.GetRequiredStringAllowBlank(host, "PidMode"),
                IpcMode: DockerJsonParsing.GetRequiredStringAllowBlank(host, "IpcMode"),
                Mounts: mounts,
                HostMounts: hostMounts,
                Tmpfs: tmpfs,
                Ulimits: ulimits,
                PortBindingCount: portBindingCount,
                DeviceCount: deviceCount);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "docker container inspect returned malformed or incomplete JSON.", ex);
        }
    }

    /// <summary>Strictly parses ONE HostConfig.Mounts entry. Every security-relevant member
    /// must be correctly typed JSON: Type, Source and Target must be present, non-blank
    /// strings (reusing the strict required-string helper), and BindOptions propagation is
    /// parsed by <see cref="ParseBindPropagation"/>. A malformed member throws instead of
    /// silently becoming "" or null — a hostile mount must never be rewritten into a shape
    /// the verifier's bind cross-check would ignore.</summary>
    private static DockerMountInfo ParseHostMountEntry(JsonElement m)
    {
        var type = DockerJsonParsing.GetRequiredString(m, "Type");
        var source = DockerJsonParsing.GetRequiredString(m, "Source");
        var target = DockerJsonParsing.GetRequiredString(m, "Target");
        return new DockerMountInfo(type, source, target, ParseBindPropagation(type, m));
    }

    /// <summary>Strictly parses BindOptions.Propagation for one HostConfig.Mounts entry.
    /// A bind entry MUST carry a usable propagation proof (the verifier requires the exact
    /// effective value rprivate), so absent or blank BindOptions/Propagation on a bind
    /// entry throws. For non-bind entries BindOptions and Propagation are optional Docker
    /// shapes: absence or explicit null normalizes to a null propagation, while a present
    /// but wrongly-typed value always throws — a wrong type is never silently turned into
    /// null.</summary>
    private static string? ParseBindPropagation(string mountType, JsonElement m)
    {
        if (!m.TryGetProperty("BindOptions", out var bindOptions) ||
            bindOptions.ValueKind == JsonValueKind.Null)
        {
            if (mountType == "bind")
                throw new InvalidOperationException(
                    "container inspect HostConfig.Mounts bind entry carries no BindOptions " +
                    "propagation proof; treating the output as hostile.");
            return null;
        }
        if (bindOptions.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                "container inspect HostConfig.Mounts BindOptions is neither an object nor " +
                "null; treating the output as hostile.");
        if (!bindOptions.TryGetProperty("Propagation", out var propagation) ||
            propagation.ValueKind == JsonValueKind.Null)
        {
            if (mountType == "bind")
                throw new InvalidOperationException(
                    "container inspect HostConfig.Mounts bind entry carries no " +
                    "BindOptions.Propagation proof; treating the output as hostile.");
            return null;
        }
        if (propagation.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException(
                "container inspect HostConfig.Mounts BindOptions.Propagation is not a " +
                "string; treating the output as hostile.");
        var value = propagation.GetString();
        if (mountType == "bind" && string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                "container inspect HostConfig.Mounts bind entry carries a blank " +
                "BindOptions.Propagation; treating the output as hostile.");
        return value;
    }
}

public sealed record DockerNetworkInfo(
    string Name,
    string Id,
    string Driver,
    bool IsReserved)
{
    public static DockerNetworkInfo FromJson(byte[] inspectJson)
    {
        try
        {
            StrictJson.EnsureNoDuplicateFields(inspectJson);
            using var doc = JsonDocument.Parse(inspectJson);
            var root = DockerJsonParsing.FirstArrayElement(doc);
            var name = DockerJsonParsing.GetRequiredString(root, "Name");
            var id = DockerJsonParsing.GetRequiredString(root, "Id");
            var driver = DockerJsonParsing.GetRequiredString(root, "Driver");
            var isReserved = new[] { "host", "bridge", "none", "default" }.Contains(
                name, StringComparer.OrdinalIgnoreCase);
            return new DockerNetworkInfo(name, id, driver, isReserved);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "docker network inspect returned malformed or incomplete JSON.", ex);
        }
    }
}

// ---- parsing ----------------------------------------------------------------

internal static class DockerJsonParsing
{
    /// <summary>Parses `docker version --format '{{json .}}'` output. Real shape:
    /// `{"Client":{"Version":…,…},"Server":{"Version":…,"Os":…,"Arch":…,…}}`. A missing or
    /// null Server object means the daemon is not reachable and fails closed.</summary>
    public static DockerDaemonInfo ParseVersionOutput(byte[] json)
    {
        try
        {
            StrictJson.EnsureNoDuplicateFields(json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Server", out var server) ||
                server.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    "docker version output carries no Server object: the daemon is not reachable.");
            var serverVersion = GetRequiredString(server, "Version");
            var osType = GetRequiredString(server, "Os");
            var arch = GetRequiredString(server, "Arch");
            return new DockerDaemonInfo(serverVersion, osType, arch,
                Rootless: false, CgroupVersion: "", CgroupDriver: "",
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "docker version returned malformed or incomplete JSON.", ex);
        }
    }

    /// <summary>Parses `docker info --format '{{json .}}'` output (PascalCase Go json tags:
    /// ServerVersion, OSType, Architecture, CgroupVersion, CgroupDriver, SecurityOptions).</summary>
    public static DockerDaemonInfo ParseFullDaemonInfo(byte[] json)
    {
        try
        {
            StrictJson.EnsureNoDuplicateFields(json);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var serverVersion = GetRequiredString(root, "ServerVersion");
            var osType = GetRequiredString(root, "OSType");
            var arch = GetRequiredString(root, "Architecture");

            var cgroupVersion = root.TryGetProperty("CgroupVersion", out var cgv) &&
                cgv.ValueKind == JsonValueKind.String
                    ? cgv.GetString() ?? ""
                    : "";
            var cgroupDriver = root.TryGetProperty("CgroupDriver", out var cgd) &&
                cgd.ValueKind == JsonValueKind.String
                    ? cgd.GetString() ?? ""
                    : "";

            var (options, securityMalformed) = AnalyzeSecurityOptions(root);
            bool rootless = !securityMalformed &&
                options.Contains("name=rootless", StringComparer.OrdinalIgnoreCase);

            // Malformed SecurityOptions contribute NO evidence: no LSM/security feature is
            // ever claimed enabled from hostile data.
            return new DockerDaemonInfo(serverVersion, osType, arch, rootless,
                cgroupVersion, cgroupDriver, options);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "docker info returned malformed or incomplete JSON.", ex);
        }
    }

    /// <summary>Strict SecurityOptions analysis shared by every parser: malformed when the
    /// field is not a string array, any member is not a string, any member is blank, or any
    /// member is duplicated (contradictory evidence). Malformed data yields NO options, so
    /// no LSM/security feature can be claimed from it.</summary>
    public static (HashSet<string> Options, bool Malformed) AnalyzeSecurityOptions(JsonElement root)
    {
        var options = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("SecurityOptions", out var secOpts))
            return (options, false); // absent: no evidence, no claim
        if (secOpts.ValueKind != JsonValueKind.Array)
            return (options, true);
        foreach (var opt in secOpts.EnumerateArray())
        {
            if (opt.ValueKind != JsonValueKind.String)
                return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
            var str = opt.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
            if (!options.Add(str))
                return (new HashSet<string>(StringComparer.OrdinalIgnoreCase), true);
        }
        return (options, false);
    }

    /// <summary>True when SecurityOptions is hostile (non-array, non-string member, blank
    /// member or duplicated evidence): no LSM may be claimed enabled from it.</summary>
    public static bool HasMalformedSecurityOptions(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return AnalyzeSecurityOptions(root).Malformed;
        }
        catch
        {
            return false;
        }
    }

    public static DockerImageInfo ParseImageInspect(byte[] inspectJson)
    {
        try
        {
            StrictJson.EnsureNoDuplicateFields(inspectJson);
            using var doc = JsonDocument.Parse(inspectJson);
            var root = DockerJsonParsing.FirstArrayElement(doc);
            var imageId = DockerJsonParsing.GetRequiredString(root, "Id");
            DockerValidation.RequireImageId(imageId, "image inspect Id");

            var repoDigests = new List<string>();
            if (root.TryGetProperty("RepoDigests", out var rd))
            {
                if (rd.ValueKind != JsonValueKind.Array)
                    throw new InvalidOperationException(
                        "image inspect RepoDigests is not an array; treating the output as hostile.");
                foreach (var d in rd.EnumerateArray())
                {
                    var str = d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    if (str is null || str.Length == 0) continue;
                    if (!str.Contains("@sha256:", StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            "image inspect RepoDigests contains a malformed entry (missing @sha256:): " +
                            StrictJson.Bounded(str));
                    var digestPart = str[(str.LastIndexOf("@sha256:", StringComparison.Ordinal) + 8)..];
                    if (!DockerValidation.IsHex64(digestPart))
                        throw new InvalidOperationException(
                            "image inspect RepoDigests contains a malformed digest: " +
                            StrictJson.Bounded(str));
                    repoDigests.Add(str);
                }
            }

            if (!root.TryGetProperty("Config", out var config))
                throw new InvalidOperationException(
                    "image inspect carries no Config object; identity and entrypoint cannot " +
                    "be verified.");
            if (config.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(
                    "image inspect Config is not an object; treating the output as hostile.");

            string configuredUser = "";
            if (config.TryGetProperty("User", out var userEl))
            {
                if (userEl.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException(
                        "image inspect Config.User is not a string; treating the output as hostile.");
                configuredUser = userEl.GetString() ?? "";
            }

            if (string.IsNullOrWhiteSpace(configuredUser))
                throw new InvalidOperationException(
                    "image has no configured user: sandbox containers must declare an explicit " +
                    "numeric non-root user (e.g. USER 1000:1000).");

            // Entrypoint fails closed on every malformed shape: present but not an array,
            // non-string members, blank members, NUL bytes. It is never silently converted
            // into an empty safe entrypoint.
            var entrypoint = new List<string>();
            if (config.TryGetProperty("Entrypoint", out var epEl))
            {
                if (epEl.ValueKind == JsonValueKind.Null)
                {
                    // explicit null entrypoint: safe (no entrypoint)
                }
                else if (epEl.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        "image inspect Config.Entrypoint is neither null nor an array; " +
                        "treating the output as hostile.");
                }
                else
                {
                    foreach (var e in epEl.EnumerateArray())
                    {
                        if (e.ValueKind != JsonValueKind.String)
                            throw new InvalidOperationException(
                                "image inspect Config.Entrypoint contains a non-string member; " +
                                "treating the output as hostile.");
                        var value = e.GetString() ?? "";
                        if (value.Length == 0 || value.Contains('\0'))
                            throw new InvalidOperationException(
                                "image inspect Config.Entrypoint contains a blank or NUL-bearing " +
                                "member; treating the output as hostile.");
                        entrypoint.Add(value);
                    }
                }
            }

            return new DockerImageInfo(imageId, repoDigests, configuredUser, entrypoint);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                "docker image inspect returned malformed or incomplete JSON.", ex);
        }
    }

    internal static JsonElement FirstArrayElement(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
            throw new InvalidOperationException(
                "expected docker inspect to return exactly one JSON object.");
        return root[0];
    }

    internal static JsonElement GetRequiredObject(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing or is not an object.");
        return prop;
    }

    internal static bool GetRequiredBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing or is not a boolean.");
        return prop.GetBoolean();
    }

    internal static long GetRequiredInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop) ||
            prop.ValueKind != JsonValueKind.Number || !prop.TryGetInt64(out var value))
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing or is not an integer.");
        return value;
    }

    internal static List<string> GetRequiredStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing or is not an array.");
        var result = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException(
                    $"docker JSON field '{propertyName}' contains a non-string member.");
            result.Add(item.GetString() ?? "");
        }
        return result;
    }

    /// <summary>
    /// Strict string-array field that accepts the real Docker representations of an empty
    /// OPTIONAL collection: a present array (string members strictly parsed), a present
    /// explicit null (normalized to empty), or a present empty array. Missing or any other
    /// type fails closed. A null never bypasses verification: normalizing to empty still
    /// fails whenever the verifier requires a non-empty result (e.g. CapDrop must contain
    /// ALL and SecurityOpt must contain no-new-privileges).
    /// </summary>
    internal static List<string> GetRequiredNullableStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arr))
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing.");
        switch (arr.ValueKind)
        {
            case JsonValueKind.Null:
                return new List<string>();
            case JsonValueKind.Array:
                var result = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException(
                            $"docker JSON field '{propertyName}' contains a non-string member.");
                    result.Add(item.GetString() ?? "");
                }
                return result;
            default:
                throw new InvalidOperationException(
                    $"docker JSON field '{propertyName}' is neither an array nor null; " +
                    "treating the output as hostile.");
        }
    }

    /// <summary>Strict nullable string-string object: present object (string values), present
    /// null (empty), or present empty object are valid; missing or any other type fails closed.
    /// When <paramref name="missingIsEmpty"/> is true (Tmpfs: absence is part of the intended
    /// Docker shape) a missing field also normalizes to empty; wrong types still fail.</summary>
    internal static Dictionary<string, string> GetRequiredNullableStringObject(
        JsonElement element, string propertyName, bool missingIsEmpty = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!element.TryGetProperty(propertyName, out var obj))
        {
            if (missingIsEmpty) return result;
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing.");
        }
        switch (obj.ValueKind)
        {
            case JsonValueKind.Null:
                return result;
            case JsonValueKind.Object:
                foreach (var p in obj.EnumerateObject())
                {
                    if (p.Value.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException(
                            $"docker JSON field '{propertyName}' carries a non-string option value.");
                    result[p.Name] = p.Value.GetString() ?? "";
                }
                return result;
            default:
                throw new InvalidOperationException(
                    $"docker JSON field '{propertyName}' is neither an object nor null; " +
                    "treating the output as hostile.");
        }
    }

    /// <summary>Strict nullable array for counting: present array (entries counted), present
    /// null (zero), or present empty array (zero); missing or any other type fails closed.</summary>
    internal static int GetRequiredNullableArrayCount(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var arr))
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing.");
        return arr.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.Array => arr.GetArrayLength(),
            _ => throw new InvalidOperationException(
                $"docker JSON field '{propertyName}' is neither an array nor null; " +
                "treating the output as hostile."),
        };
    }

    /// <summary>Strict nullable object for counting: present object (properties counted),
    /// present null (zero), or present empty object (zero); missing or any other type fails
    /// closed.</summary>
    internal static int GetRequiredNullableObjectCount(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var obj))
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing.");
        return obj.ValueKind switch
        {
            JsonValueKind.Null => 0,
            JsonValueKind.Object => obj.EnumerateObject().Count(),
            _ => throw new InvalidOperationException(
                $"docker JSON field '{propertyName}' is neither an object nor null; " +
                "treating the output as hostile."),
        };
    }

    internal static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetRequiredStringAllowBlank(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is blank or empty.");
        return value;
    }

    /// <summary>Requires the field to be present and a string, tolerating a blank value
    /// (PidMode and IpcMode are legitimately empty strings in docker inspect).</summary>
    internal static string GetRequiredStringAllowBlank(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is missing.");
        var value = prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
        if (value is null)
            throw new InvalidOperationException(
                $"required docker JSON field '{propertyName}' is not a string.");
        return value;
    }

    internal static List<string> GetStringArray(JsonElement element, string propertyName)
    {
        var result = new List<string>();
        if (element.TryGetProperty(propertyName, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                    result.Add(s);
        return result;
    }
}
