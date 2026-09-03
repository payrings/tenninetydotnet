using System.Text;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Tests;

/// <summary>
/// Nullable Docker inspect field parsing (Defect B): real Docker versions represent an empty
/// OPTIONAL HostConfig collection as explicit null (e.g. "CapAdd": null). The parser must
/// distinguish a valid explicit null (empty), a valid empty array/object, a malformed wrong
/// type (fail closed) and a missing security-relevant field (fail closed) — WITHOUT weakening
/// the effective-hardening verifier: a null CapDrop still fails because ALL is absent, and a
/// null SecurityOpt still fails because no-new-privileges is absent.
/// </summary>
public class DockerInspectParsingTests
{
    private static readonly string ImageId = "sha256:" + new string('a', 64);
    private static readonly string ContainerId = new('1', 64);
    private const string Source = "/srv/tenninety/attempt-1";

    /// <summary>Realistic detailed inspect fixture with every security-relevant HostConfig
    /// member rendered exactly as production Docker emits it; each member is overridable.
    /// A null override REMOVES the member entirely (missing-field case).</summary>
    private static string DetailedJson(
        string? capAdd = "null", string? capDrop = "[\"ALL\"]",
        string? securityOpt = "[\"no-new-privileges\"]",
        string? devices = "[]", string? portBindings = "{}",
        string? tmpfs = "{\"/tmp\":\"size=512m,nosuid,nodev,noexec\",\"/home/tenninety\":\"size=256m,nosuid,nodev\"}",
        string? ulimits = "[{\"Name\":\"nofile\",\"Soft\":4096,\"Hard\":8192}]",
        string? hostMounts = null,
        bool includeHostConfig = true)
    {
        const string Missing = "@@MISSING@@";
        // A null override removes the member (absent field); the MISSING sentinel also
        // removes it (missing-field case); anything else is rendered verbatim.
        string Member(string name, string? value) =>
            value is null || value == Missing
                ? "" : "\"" + name + "\":" + value;
        var host = includeHostConfig
            ? "\"HostConfig\":{" +
              string.Join(",", new[]
              {
                  Member("CapAdd", capAdd == "MISSING" ? Missing : capAdd),
                  Member("CapDrop", capDrop == "MISSING" ? Missing : capDrop),
                  Member("SecurityOpt", securityOpt == "MISSING" ? Missing : securityOpt),
                  "\"Privileged\":false",
                  "\"ReadonlyRootfs\":true",
                  Member("Devices", devices == "MISSING" ? Missing : devices),
                  Member("PortBindings", portBindings == "MISSING" ? Missing : portBindings),
                  Member("Tmpfs", tmpfs == "MISSING" ? Missing : tmpfs),
                  Member("Ulimits", ulimits == "MISSING" ? Missing : ulimits),
                  "\"NetworkMode\":\"none\"",
                  "\"PidMode\":\"\"",
                  "\"IpcMode\":\"private\"",
                  "\"NanoCpus\":1000000000",
                  "\"Memory\":268435456",
                  "\"PidsLimit\":64",
              }.Where(part => part.Length > 0)) +
              (hostMounts is null ? "" : ",\"Mounts\":" + hostMounts) +
              "},"
            : "";
        return "[" +
               "{\"Id\":\"" + ContainerId + "\",\"Image\":\"" + ImageId + "\"," +
               "\"State\":{\"Running\":true,\"Paused\":false,\"OOMKilled\":false,\"ExitCode\":0}," +
               host +
               "\"Config\":{\"User\":\"1000:1000\",\"WorkingDir\":\"/workspace\"}," +
               "\"Mounts\":[{\"Type\":\"bind\",\"Source\":\"" + Source +
               "\",\"Destination\":\"/workspace\",\"Mode\":\"\",\"RW\":true,\"Propagation\":\"rprivate\"}]," +
               "\"NetworkSettings\":{\"Ports\":{}}}" +
               "]";
    }

    private static DockerContainerDetailed Parse(string json) =>
        DockerContainerDetailed.FromJson(Encoding.UTF8.GetBytes(json));

    // ---- valid null / empty representations parse successfully ---------------------

    [Fact]
    public void Null_capadd_parses_as_empty()
    {
        var detailed = Parse(DetailedJson(capAdd: "null"));
        Assert.Empty(detailed.CapAdd);
    }

    [Fact]
    public void Empty_array_capadd_parses_as_empty()
    {
        var detailed = Parse(DetailedJson(capAdd: "[]"));
        Assert.Empty(detailed.CapAdd);
    }

    [Fact]
    public void Null_devices_parses_as_zero()
    {
        var detailed = Parse(DetailedJson(devices: "null"));
        Assert.Equal(0, detailed.DeviceCount);
    }

    [Fact]
    public void Null_port_bindings_parses_as_zero()
    {
        var detailed = Parse(DetailedJson(portBindings: "null"));
        Assert.Equal(0, detailed.PortBindingCount);
    }

    [Fact]
    public void Null_tmpfs_parses_as_empty_and_missing_tmpfs_is_accepted_by_the_parser()
    {
        Assert.Empty(Parse(DetailedJson(tmpfs: "null")).Tmpfs);
        Assert.Empty(Parse(DetailedJson(tmpfs: "{}")).Tmpfs);
        // A missing Tmpfs member is part of the intended Docker shape (normalized to empty);
        // the VERIFIER still requires the two exact bounded entries, so preflight fails.
        Assert.Empty(Parse(DetailedJson(tmpfs: "MISSING")).Tmpfs);
    }

    [Fact]
    public void Null_capdrop_and_null_securityopt_parse_but_must_fail_verification()
    {
        // Parsing succeeds (valid null = empty), but the effective-hardening verifier must
        // fail because ALL / no-new-privileges are absent.
        Assert.Empty(Parse(DetailedJson(capDrop: "null")).CapDrop);
        Assert.Empty(Parse(DetailedJson(securityOpt: "null")).SecurityOpt);
    }

    [Fact]
    public void Null_host_mounts_and_absent_host_mounts_are_valid_empty()
    {
        Assert.Empty(Parse(DetailedJson(hostMounts: "null")).HostMounts);
        Assert.Empty(Parse(DetailedJson()).HostMounts);
        var present = Parse(DetailedJson(hostMounts:
            "[{\"Type\":\"bind\",\"Source\":\"" + Source +
            "\",\"Target\":\"/workspace\",\"BindOptions\":{\"Propagation\":\"rprivate\"}}]"));
        Assert.Single(present.HostMounts);
    }

    [Fact]
    public void Null_ulimits_parses_as_empty()
    {
        Assert.Empty(Parse(DetailedJson(ulimits: "null")).Ulimits);
    }

    // ---- malformed / missing types still fail closed ---------------------------------

    [Fact]
    public void Wrong_types_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(capAdd: "\"NET_ADMIN\"")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(capAdd: "{}")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(capDrop: "\"ALL\"")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(securityOpt: "\"x\"")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(devices: "{}")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(devices: "3")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(portBindings: "[]")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(portBindings: "\"x\"")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(tmpfs: "[]")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(tmpfs: "\"x\"")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(ulimits: "{}")));
        // A present HostConfig.Mounts array with a non-object entry fails.
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(hostMounts: "[5]")));
    }

    [Fact]
    public void Missing_security_relevant_fields_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(capAdd: "MISSING")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(capDrop: "MISSING")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(securityOpt: "MISSING")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(devices: "MISSING")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(portBindings: "MISSING")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(ulimits: "MISSING")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(includeHostConfig: false)));
    }

    [Fact]
    public void Non_string_array_members_fail_closed()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(capAdd: "[5]")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(capDrop: "[null]")));
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(securityOpt: "[{}]")));
    }

    [Fact]
    public void Existing_array_object_fixtures_continue_to_work()
    {
        var detailed = Parse(DetailedJson(
            capAdd: "[]", capDrop: "[\"ALL\"]",
            securityOpt: "[\"no-new-privileges\"]",
            devices: "[]", portBindings: "{}"));
        Assert.Equal(ImageId, detailed.ImageId);
        Assert.Equal(ContainerId, detailed.ContainerId);
        Assert.True(detailed.ReadonlyRootfs);
        Assert.Contains("ALL", detailed.CapDrop);
        Assert.Contains("no-new-privileges", detailed.SecurityOpt);
        Assert.Equal(0, detailed.DeviceCount);
        Assert.Equal(0, detailed.PortBindingCount);
        var bind = Assert.Single(detailed.Mounts);
        Assert.Equal("/workspace", bind.Destination);
        Assert.True(bind.Rw);
        Assert.Equal("rprivate", bind.Propagation);
    }

    [Fact]
    public void Effective_inspection_accepts_both_empty_representations()
    {
        // Both a valid empty array AND a valid explicit null must be acceptable by the
        // preflight verifier for CapAdd/Devices/PortBindings — the integration-test
        // assertion path. (CapDrop/SecurityOpt nulls must still FAIL the verifier.)
        var emptyArray = Parse(DetailedJson(capAdd: "[]", devices: "[]", portBindings: "{}"));
        var explicitNull = Parse(DetailedJson(capAdd: "null", devices: "null", portBindings: "null"));
        Assert.Empty(emptyArray.CapAdd);
        Assert.Empty(explicitNull.CapAdd);
        Assert.Equal(0, emptyArray.DeviceCount);
        Assert.Equal(0, explicitNull.DeviceCount);
        Assert.Equal(0, emptyArray.PortBindingCount);
        Assert.Equal(0, explicitNull.PortBindingCount);
    }

    // ---- HostConfig.Mounts strict entry parsing ----------------------------------------
    //
    // A present HostConfig.Mounts array must be entirely trustworthy: every entry is an
    // object whose security-relevant members (Type, Source, Target, BindOptions.Propagation)
    // are strictly typed. A missing, null, blank or wrongly-typed member fails closed —
    // it must never be silently rewritten into "" (or null), because the preflight
    // cross-check looks for Type == "bind": rewriting a hostile "Type": 7 into "" makes
    // the mount invisible and silently skips the secondary representation check.

    /// <summary>Builds a ONE-entry HostConfig.Mounts array where each member is rendered
    /// from a raw JSON fragment; a null value omits the member entirely (missing-field
    /// case). Defaults render the exact production bind shape.</summary>
    private static string HostMountEntry(
        string? type = "\"bind\"",
        string? source = "\"" + Source + "\"",
        string? target = "\"/workspace\"",
        string? bindOptions = "{\"Propagation\":\"rprivate\"}")
    {
        string Member(string name, string? value) =>
            value is null ? "" : "\"" + name + "\":" + value;
        return "[{" + string.Join(",", new[]
        {
            Member("Type", type),
            Member("Source", source),
            Member("Target", target),
            Member("BindOptions", bindOptions),
        }.Where(part => part.Length > 0)) + "}]";
    }

    /// <summary>Renders the HostConfig.Mounts array with one member replaced by a raw JSON
    /// fragment (or omitted entirely when <paramref name="value"/> is null).</summary>
    private static string HostMountForMember(string member, string? value) => member switch
    {
        "Type" => HostMountEntry(type: value),
        "Source" => HostMountEntry(source: value),
        "Target" => HostMountEntry(target: value),
        "BindOptions" => HostMountEntry(bindOptions: value),
        "BindOptions.Propagation" => HostMountEntry(bindOptions: "{\"Propagation\":" + value + "}"),
        _ => throw new ArgumentException("unknown HostConfig mount member: " + member),
    };

    [Fact]
    public void Empty_host_mounts_array_is_a_valid_empty_representation()
    {
        Assert.Empty(Parse(DetailedJson(hostMounts: "[]")).HostMounts);
    }

    [Fact]
    public void Realistic_bind_entry_parses_exact_members()
    {
        var mount = Assert.Single(Parse(DetailedJson(hostMounts: HostMountEntry())).HostMounts);
        Assert.Equal("bind", mount.Type);
        Assert.Equal(Source, mount.Source);
        Assert.Equal("/workspace", mount.Target);
        Assert.Equal("rprivate", mount.Propagation);
    }

    [Fact]
    public void Non_bind_entry_without_bind_options_is_a_valid_optional_representation()
    {
        // A well-typed non-bind entry may omit BindOptions: absence normalizes to a null
        // propagation, and the verifier requires an exact propagation only from the bind
        // representation (the effective top-level mount remains the primary proof).
        var mount = Assert.Single(Parse(DetailedJson(hostMounts: HostMountEntry(
            type: "\"volume\"", source: "\"local-volume\"", target: "\"/data\"",
            bindOptions: null))).HostMounts);
        Assert.Equal("volume", mount.Type);
        Assert.Equal("/data", mount.Target);
        Assert.Null(mount.Propagation);
    }

    [Theory]
    [InlineData("Type")]
    [InlineData("Source")]
    [InlineData("Target")]
    public void Missing_required_host_mount_members_fail_closed(string member)
    {
        Assert.Throws<InvalidOperationException>(
            () => Parse(DetailedJson(hostMounts: HostMountForMember(member, null))));
    }

    [Theory]
    [InlineData("Type")]
    [InlineData("Source")]
    [InlineData("Target")]
    public void Blank_required_host_mount_members_fail_closed(string member)
    {
        Assert.Throws<InvalidOperationException>(
            () => Parse(DetailedJson(hostMounts: HostMountForMember(member, "\"   \""))));
    }

    [Theory]
    [InlineData("Type", "7")]
    [InlineData("Type", "null")]
    [InlineData("Type", "true")]
    [InlineData("Type", "[]")]
    [InlineData("Type", "{}")]
    [InlineData("Source", "7")]
    [InlineData("Source", "null")]
    [InlineData("Source", "true")]
    [InlineData("Target", "7")]
    [InlineData("Target", "null")]
    [InlineData("BindOptions", "7")]
    [InlineData("BindOptions", "true")]
    [InlineData("BindOptions", "[]")]
    [InlineData("BindOptions.Propagation", "7")]
    [InlineData("BindOptions.Propagation", "null")]
    [InlineData("BindOptions.Propagation", "true")]
    [InlineData("BindOptions.Propagation", "[]")]
    public void Malformed_host_mount_members_fail_closed(string member, string value)
    {
        Assert.Throws<InvalidOperationException>(
            () => Parse(DetailedJson(hostMounts: HostMountForMember(member, value))));
    }

    [Fact]
    public void Bind_entry_without_usable_propagation_proof_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(hostMounts:
            HostMountEntry(bindOptions: null))));                       // BindOptions absent
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(hostMounts:
            HostMountEntry(bindOptions: "null"))));                     // BindOptions explicit null
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(hostMounts:
            HostMountEntry(bindOptions: "{}"))));                       // Propagation absent
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(hostMounts:
            HostMountEntry(bindOptions: "{\"Propagation\":null}"))));   // Propagation explicit null
        Assert.Throws<InvalidOperationException>(() => Parse(DetailedJson(hostMounts:
            HostMountEntry(bindOptions: "{\"Propagation\":\"   \"}")))); // Propagation blank
    }
}
