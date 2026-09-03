using Tenninety.Core.Models;
using Tenninety.Git;

namespace Tenninety.Execution.Sandbox;

/// <summary>Startup recovery under the repository daemon lock. It inventories only containers
/// carrying this instance/repository identity and deletes only workspaces present in the trusted
/// ownership journal. Any indeterminate cleanup quarantines the records and blocks execution.</summary>
internal sealed class SandboxRecoveryService
{
    private readonly IGitService _git;
    private readonly TenNinetyConfig _config;
    private readonly Func<IDockerCliTransport>? _transportFactory;

    internal SandboxRecoveryService(
        IGitService git,
        TenNinetyConfig config,
        Func<IDockerCliTransport>? transportFactory = null)
    {
        _git = git;
        _config = config;
        _transportFactory = transportFactory;
    }

    internal async Task<SandboxRecoveryInfo> RecoverAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        if (_config.NormalizedProviderMode == "mock" || _config.Sandbox.IsUnsafeHost)
            return new SandboxRecoveryInfo
            {
                Status = "not-required",
                LastRunUtc = now,
                Detail = "Docker recovery is not required for the effective execution mode.",
            };

        ct.ThrowIfCancellationRequested();
        var journal = new SandboxResourceJournal(_git.RepoPath);
        var records = journal.ReadAll();
        var quarantined = new List<string>();
        var removedContainers = 0;
        var removedWorkspaces = 0;
        var transport = _transportFactory?.Invoke() ?? new DockerCliProcessTransport();
        if (transport is not IDisposable disposable)
            throw new InvalidOperationException("sandbox recovery Docker transport must be disposable.");
        IReadOnlyList<string> containerIds = [];
        try
        {
            var cli = new DockerCli(transport);
            var scope = DockerRecoveryScope.Create(
                "tenninety", SandboxPolicy.RepositoryIdentity(_git.RepoPath));
            containerIds = (await cli.ListRecoveryContainersAsync(scope, ct))
                .Concat(records
                    .Where(record => record.ContainerId is not null)
                    .Select(record => record.ContainerId!))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList()
                .AsReadOnly();
            foreach (var containerId in containerIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await RemoveContainerAsync(cli, containerId);
                    removedContainers++;
                }
                catch
                {
                    quarantined.Add("container-" + containerId[..12]);
                }
            }

            if (quarantined.Count == 0)
            {
                foreach (var record in records)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var rootKind = TrustedWorkspaceDeletion.InspectEntryNoFollow(
                            record.ManagedRoot);
                        if (rootKind == TrustedWorkspaceDeletion.ManagedEntryKind.RealDirectory)
                        {
                            TrustedWorkspaceDeletion.DeleteManagedChildDirectory(
                                record.AttemptRoot, record.ManagedRoot);
                            if (record.OwnedManagedRoot)
                                TrustedWorkspaceDeletion.DeleteEmptyOwnedDirectory(record.ManagedRoot);
                        }
                        else if (rootKind != TrustedWorkspaceDeletion.ManagedEntryKind.Absent ||
                                 !record.OwnedManagedRoot)
                        {
                            throw new InvalidOperationException(
                                "configured managed root is missing during recovery.");
                        }
                        journal.Complete(record.Id);
                        removedWorkspaces++;
                    }
                    catch
                    {
                        var name = Path.GetFileName(record.AttemptRoot);
                        quarantined.Add(name);
                        journal.RecordFailure(record.Id, "startup workspace cleanup failed");
                    }
                }
            }
            else
            {
                foreach (var record in records)
                    journal.RecordFailure(record.Id, "startup container cleanup was not proven");
            }
        }
        finally
        {
            try { disposable.Dispose(); }
            catch { quarantined.Add("docker-transport"); }
        }

        var status = quarantined.Count > 0
            ? "quarantined"
            : containerIds.Count + records.Count > 0 ? "recovered" : "clean";
        return new SandboxRecoveryInfo
        {
            Status = status,
            LastRunUtc = now,
            ContainersFound = containerIds.Count,
            ContainersRemoved = removedContainers,
            WorkspacesFound = records.Count,
            WorkspacesRemoved = removedWorkspaces,
            Quarantined = quarantined.Distinct(StringComparer.Ordinal).Take(1000).ToList(),
            Detail = quarantined.Count > 0
                ? "Scoped sandbox cleanup was not proven; execution is refused."
                : status == "recovered"
                    ? "Scoped stale sandbox resources were removed before execution."
                    : "No stale scoped sandbox resources were found.",
        };
    }

    private static async Task RemoveContainerAsync(DockerCli cli, string containerId)
    {
        var state = await cli.TryInspectContainerAsync(containerId, CancellationToken.None);
        // Positively absent (typed inspect, "No such container"): removal is already proven
        // and a `docker rm` on a nonexistent container would be a false quarantine on some
        // daemons.
        if (state is null) return;
        if (state.Running)
        {
            try
            {
                await cli.StopContainerAsync(
                    containerId, TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch
            {
                await cli.KillContainerAsync(containerId, CancellationToken.None);
            }
            state = await cli.TryInspectContainerAsync(containerId, CancellationToken.None);
            if (state?.Running == true)
                await cli.KillContainerAsync(containerId, CancellationToken.None);
        }
        await cli.RemoveContainerAsync(containerId, CancellationToken.None);
    }
}
