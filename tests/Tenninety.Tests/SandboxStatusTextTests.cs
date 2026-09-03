using Tenninety.Cli;
using Tenninety.Core.Models;

namespace Tenninety.Tests;

/// <summary>
/// Phase 1 status-truthfulness contract: the status screen must distinguish the CONFIGURED
/// sandbox mode from the EFFECTIVE execution mode. In Phase 1 no Docker execution exists, so
/// docker mode must be reported as configured-but-not-active with legacy host execution, mock
/// mode must say Docker is not used, and unsafe-host must stay conspicuous. Image references
/// may only be called "configured" when they are actually digest-pinned.
/// </summary>
public class SandboxConfigStatusTextTests
{
    private static TenNinetyConfig MockConfig() => new();

    private static TenNinetyConfig LiveDockerConfig(bool pinned = true)
    {
        var config = new TenNinetyConfig { ProviderMode = "aider" };
        if (pinned)
        {
            var digest = new string('a', 64);
            config.Sandbox.Roles.Coder.Image = $"ghcr.io/tenninety/coder-aider@sha256:{digest}";
            config.Sandbox.Roles.Reviewer.Image = $"ghcr.io/tenninety/reviewer@sha256:{digest}";
            config.Sandbox.Roles.Tester.Image = $"ghcr.io/tenninety/tester-dotnet@sha256:{digest}";
        }
        else
        {
            config.Sandbox.Roles.Coder.Image = "tenninety/coder:latest";
            config.Sandbox.Roles.Reviewer.Image = "tenninety/reviewer:latest";
            config.Sandbox.Roles.Tester.Image = "tenninety/tester:latest";
        }
        return config;
    }

    // ---- effective mode ---------------------------------------------------------

    [Fact]
    public void Mock_mode_reports_in_process_execution_without_docker()
    {
        var status = SandboxStatusText.SandboxMode(MockConfig());
        Assert.Contains("mock", status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Docker not used", status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(status.Warning);
        // It must not pretend host execution or docker isolation is happening.
        Assert.DoesNotContain("isolated", status.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Docker_mode_reports_active_role_isolation()
    {
        var status = SandboxStatusText.SandboxMode(LiveDockerConfig());
        Assert.Contains("docker", status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isolation active", status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(status.Warning);
    }

    [Fact]
    public void Unsafe_host_mode_stays_conspicuous_and_names_the_lack_of_isolation()
    {
        var config = LiveDockerConfig();
        config.Sandbox.Mode = "unsafe-host";
        var status = SandboxStatusText.SandboxMode(config);
        Assert.Contains("unsafe-host", status.Text);
        Assert.Contains("NO container isolation", status.Text);
        Assert.True(status.Warning);
    }

    // ---- image references -------------------------------------------------------

    [Fact]
    public void Mock_mode_does_not_require_images()
    {
        var status = SandboxStatusText.SandboxImages(MockConfig());
        Assert.Contains("mock", status.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(status.Warning);
    }

    [Fact]
    public void Unsafe_host_mode_does_not_require_images()
    {
        var config = LiveDockerConfig();
        config.Sandbox.Mode = "unsafe-host";
        var status = SandboxStatusText.SandboxImages(config);
        Assert.Contains("unsafe-host", status.Text);
        Assert.False(status.Warning);
    }

    [Fact]
    public void Pinned_image_references_are_labelled_configured()
    {
        var status = SandboxStatusText.SandboxImages(LiveDockerConfig(pinned: true));
        Assert.Contains("digest-pinned", status.Text);
        Assert.Contains("configured", status.Text);
        Assert.False(status.Warning);
    }

    [Fact]
    public void Non_empty_but_unpinned_images_are_never_called_configured()
    {
        var status = SandboxStatusText.SandboxImages(LiveDockerConfig(pinned: false));
        Assert.Contains("values present", status.Text);
        Assert.Contains("NOT digest-pinned", status.Text);
        Assert.DoesNotContain("configured", status.Text);
        Assert.True(status.Warning);
    }

    [Fact]
    public void Blank_images_are_reported_as_not_set()
    {
        var config = LiveDockerConfig(pinned: false);
        config.Sandbox.Roles.Coder.Image = "";
        var status = SandboxStatusText.SandboxImages(config);
        Assert.Equal("not set", status.Text);
        Assert.True(status.Warning);
    }

    [Fact]
    public void A_single_unpinned_reference_taints_the_whole_summary()
    {
        var config = LiveDockerConfig(pinned: true);
        config.Sandbox.Roles.Tester.Image = "sha256:" + new string('a', 63); // one short digest
        var status = SandboxStatusText.SandboxImages(config);
        Assert.Contains("NOT digest-pinned", status.Text);
        Assert.True(status.Warning);
    }
}
