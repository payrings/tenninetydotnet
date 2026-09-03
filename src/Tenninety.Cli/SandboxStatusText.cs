using Tenninety.Core.Models;

namespace Tenninety.Cli;

/// <summary>
/// Pure formatting of the sandbox portion of the status screen. Mock mode never implies Docker
/// use, unsafe-host remains conspicuous, and Docker status distinguishes configured posture from
/// persisted startup recovery evidence.
/// </summary>
public static class SandboxStatusText
{
    public sealed record SandboxStatusValue(string Text, bool Warning);

    /// <summary>Effective execution posture: what will actually run this attempt.</summary>
    public static SandboxStatusValue SandboxMode(TenNinetyConfig config)
    {
        var provider = config.NormalizedProviderMode;
        if (provider == "mock")
            return new SandboxStatusValue(
                "in-process mock (Docker not used)", Warning: false);
        var mode = config.Sandbox.NormalizedMode;
        if (mode == "unsafe-host")
            return new SandboxStatusValue(
                "unsafe-host: execution runs directly on the host with NO container isolation", Warning: true);
        return new SandboxStatusValue(
            "Docker isolation active for Coder, Reviewer and Tester", Warning: false);
    }

    /// <summary>Truthful summary of the configured role image references.</summary>
    public static SandboxStatusValue SandboxImages(TenNinetyConfig config)
    {
        if (config.NormalizedProviderMode == "mock")
            return new SandboxStatusValue("not required (mock mode)", Warning: false);
        if (config.Sandbox.IsUnsafeHost)
            return new SandboxStatusValue("not required (unsafe-host mode)", Warning: false);

        var images = new[]
        {
            config.Sandbox.Roles.Coder.Image,
            config.Sandbox.Roles.Reviewer.Image,
            config.Sandbox.Roles.Tester.Image,
        };
        if (images.All(SandboxConfig.IsPinnedImageReference))
            return new SandboxStatusValue("digest-pinned references configured", Warning: false);
        if (images.All(i => !string.IsNullOrWhiteSpace(i)))
            return new SandboxStatusValue(
                "values present but NOT digest-pinned (live docker mode will refuse them)", Warning: true);
        return new SandboxStatusValue("not set", Warning: true);
    }

    public static SandboxStatusValue SandboxRecovery(
        TenNinetyConfig config, SandboxRecoveryInfo recovery)
    {
        if (config.NormalizedProviderMode == "mock" || config.Sandbox.IsUnsafeHost)
            return new SandboxStatusValue("not required", Warning: false);
        return recovery.Status switch
        {
            "clean" => new SandboxStatusValue("clean; no stale resources found", Warning: false),
            "recovered" => new SandboxStatusValue(
                $"recovered {recovery.ContainersRemoved} container(s), " +
                $"{recovery.WorkspacesRemoved} workspace(s)", Warning: false),
            "quarantined" => new SandboxStatusValue(
                $"QUARANTINED: {recovery.Quarantined.Count} unresolved resource(s)", Warning: true),
            _ => new SandboxStatusValue("not run yet", Warning: true),
        };
    }
}
