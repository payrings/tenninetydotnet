using Tenninety.Core.Models;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Production Docker CLI sandbox runtime: creates hardened containers from validated
/// <see cref="SandboxSpec"/> values through the typed <see cref="DockerCli"/> adapter.
/// There is no escape hatch for raw Docker arguments, shell commands, or extra flags.
///
/// Ordering contract: the spec and runtime network mapping are validated first, the image is
/// inspected and verified, and the physical workspace is revalidated AFTER all asynchronous
/// image/network operations and IMMEDIATELY before the typed create request is built and
/// submitted — there is no await or unrelated operation between final revalidation and the
/// create call (TOCTOU defense).
/// </summary>
public sealed class DockerCliSandboxRuntime : ISandboxRuntime
{
    private readonly DockerCli _cli;
    private readonly SandboxConfig _config;
    private readonly string _authoritativeRepositoryPath;
    private readonly string _managedRoot;

    public DockerCliSandboxRuntime(
        DockerCli cli,
        SandboxConfig config,
        string authoritativeRepositoryPath,
        string managedRoot)
    {
        _cli = cli;
        _config = config;
        _authoritativeRepositoryPath = authoritativeRepositoryPath;
        _managedRoot = managedRoot;
    }

    public async Task<ISandboxSession> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
    {
        // 1. Validate the spec first and capture the internal evidence token — the create
        //    request factory REQUIRES this proof, so no unvalidated value can reach Docker.
        var evidence = spec.ValidateAndCapture();

        // 2. Fail fast on an invalid runtime network mapping BEFORE any Docker invocation
        //    (reserved names are unreachable here even if a hostile SandboxConfig object
        //    bypassed config validation). The factory resolves the network itself from the
        //    same validated (role, policy) tuple — this eager check shares that exact path.
        _ = DockerCreateRequest.ResolveNetworkName(evidence.Role, evidence.Network, _config);

        // 3. Inspect and resolve the image (pinned-reference and digest evidence verified
        //    inside the adapter).
        var imageInfo = await _cli.InspectImageAsync(spec.Image, ct);

        // 4. Verify the explicit numeric non-root identity.
        var identity = ContainerIdentity.Parse(imageInfo.ConfiguredUser);
        if (imageInfo.ConfigEntrypoint.Count > 0)
            throw new InvalidOperationException(
                $"image '{imageInfo.ImageId[..Math.Min(19, imageInfo.ImageId.Length)]}…' declares an " +
                "ENTRYPOINT; the fixed waiting command could be reinterpreted and cannot be " +
                "guaranteed. Build sandbox images without ENTRYPOINT.");

        var containerName = $"tenninety-{spec.Role.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";

        // 5. Restore's accepted network identity is checked again immediately before the
        //    final workspace revalidation/create sequence. A same-name replacement cannot
        //    silently use an unaccepted network.
        if (spec.Role == SandboxRole.Restore)
        {
            var accepted = _config.Roles.Tester.Restore.Acceptance.NetworkId;
            var network = await _cli.InspectNetworkAsync(
                _config.Roles.Tester.Restore.NetworkName, ct);
            if (network is null || !string.Equals(network.Id, accepted, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the restore network identity no longer matches the operator acceptance record.");
        }

        // 6. FINAL physical workspace revalidation: after all asynchronous operations and
        //    immediately before request construction and the create call. No await in between.
        var revalidated = RevalidateWorkspace(spec);

        // 7. Build the typed request (from validated evidence; the factory resolves the
        //    network) and create the container.
        var request = DockerCreateRequest.FromSpec(
            evidence, _config, imageInfo.ImageId, identity, containerName, revalidated);
        string containerId;
        try
        {
            containerId = await _cli.CreateContainerAsync(request, ct);
        }
        catch (Exception createFailure)
        {
            // A timed-out, cancelled or otherwise failed `docker create` may still have
            // created the container on the daemon without returning its ID. Bounded
            // label-scoped cleanup removes any container carrying this attempt's exact
            // management identity; the failure message stays bounded.
            var leakCleanup = await TryRemoveLabelScopedContainersAsync(request);
            if (leakCleanup is null) throw;
            throw new InvalidOperationException(
                createFailure.Message +
                " A possible attempt-created container could not be proven removed: " +
                leakCleanup, createFailure);
        }

        // 8. Start, then inspect and prove running + identity.
        try
        {
            await _cli.StartContainerAsync(containerId, ct);
            var state = await _cli.InspectContainerAsync(containerId, expectedImageId: imageInfo.ImageId, ct);
            if (!state.Running)
                throw new InvalidOperationException(
                    $"container {containerId[..Math.Min(12, containerId.Length)]} is not running " +
                    $"after start (paused={state.Paused}, exitCode={state.ExitCode}).");

            return new DockerCliSandboxSession(_cli, containerId, spec.Role, spec.Timeout);
        }
        catch (Exception primary)
        {
            // 9. Bounded cleanup; surface both primary and cleanup failures when cleanup
            //    is not proven.
            var cleanupError = await TryCleanupAsync(containerId);
            if (cleanupError is null) throw;
            throw new InvalidOperationException(
                primary.Message + " Cleanup also failed and a scoped container may remain: " +
                cleanupError, primary);
        }
    }

    private string RevalidateWorkspace(SandboxSpec spec)
    {
        var workspace = spec.HostWorkspacePath
            ?? throw new InvalidOperationException(
                "sandbox spec is missing a validated workspace path — cannot revalidate before mount.");
        // Full revalidation against the managed root and the authoritative repository:
        // physical containment, no reparse points, no overlap. Comma-containing sources are
        // rejected by DockerValidation when the request is built (mount-grammar injection).
        return ValidatedSandboxWorkspacePath.Create(
            workspace.Value, _managedRoot, _authoritativeRepositoryPath).Value;
    }

    private async Task<string?> TryCleanupAsync(string containerId)
    {
        try
        {
            // True = removed; false = positively absent. Both prove cleanup.
            await _cli.RemoveContainerAsync(containerId, CancellationToken.None);
            return null;
        }
        catch (Exception cleanupFailure)
        {
            return cleanupFailure.Message;
        }
    }

    /// <summary>Bounded best-effort removal of containers carrying this attempt's exact
    /// management identity after a failed/cancelled create. Returns null when none remain or
    /// all were removed with proof; otherwise a bounded description of the failure.</summary>
    private async Task<string?> TryRemoveLabelScopedContainersAsync(DockerCreateRequest request)
    {
        try
        {
            var scope = DockerContainerScope.FromManagementIdentity(request.Labels);
            var ids = await _cli.ListContainersAsync(scope, CancellationToken.None);
            string? firstFailure = null;
            foreach (var id in ids)
            {
                try
                {
                    await _cli.RemoveContainerAsync(id, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    firstFailure ??= ex.GetType().Name;
                }
            }
            return firstFailure;
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }
}
