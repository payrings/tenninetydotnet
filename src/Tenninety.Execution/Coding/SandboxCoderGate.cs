using System.Globalization;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Execution.Coding;

/// <summary>
/// Trusted Docker Coder coordinator. It materializes an exact committed candidate in a fresh
/// workspace, runs one fixed coding-tool command in a hardened model-network container, proves
/// removal, then scans and promotes only the validated opaque patch while the existing daemon
/// lock lease remains live. No sandbox process receives the authoritative repository path.
/// </summary>
public sealed class SandboxCoderGate : ICoderAgent
{
    private readonly IGitService _git;
    private readonly TenNinetyConfig _config;
    private readonly DaemonLockLease _lease;
    private readonly Action<string>? _log;
    private readonly Func<IDockerCliTransport>? _transportFactory;
    private readonly Func<DockerCli, string, ISandboxRuntime>? _runtimeFactory;
    private readonly Func<DockerCli, string, DockerSandboxPreflight>? _preflightFactory;
    private readonly Func<string, Task>? _deleteWorkspaceOverride;
    private readonly Func<CoderRunContext, SandboxCommand>? _coderCommandFactory;

    public SandboxCoderGate(
        IGitService authoritativeGit,
        TenNinetyConfig config,
        DaemonLockLease lease,
        Action<string>? log = null)
        : this(authoritativeGit, config, lease, log, null, null, null, null, null)
    {
    }

    /// <summary>Internal seam (InternalsVisibleTo): a deterministic guest fixture command can
    /// replace the production CoderToolPlan command so live gate tests can prove real
    /// materialization/spec/create/exec/cleanup-before-scan/promotion without depending on
    /// tool/model configuration. Production construction always passes null.</summary>
    internal SandboxCoderGate(
        IGitService authoritativeGit,
        TenNinetyConfig config,
        DaemonLockLease lease,
        Action<string>? log,
        Func<IDockerCliTransport>? transportFactory,
        Func<DockerCli, string, ISandboxRuntime>? runtimeFactory,
        Func<DockerCli, string, DockerSandboxPreflight>? preflightFactory,
        Func<string, Task>? deleteWorkspaceOverride,
        Func<CoderRunContext, SandboxCommand>? coderCommandFactory = null)
    {
        _git = authoritativeGit ?? throw new ArgumentNullException(nameof(authoritativeGit));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _log = log;
        _transportFactory = transportFactory;
        _runtimeFactory = runtimeFactory;
        _preflightFactory = preflightFactory;
        _deleteWorkspaceOverride = deleteWorkspaceOverride;
        _coderCommandFactory = coderCommandFactory;
    }

    private sealed class RunState
    {
        public string Stage = "initialization";
        public string? ManagedRoot;
        public string? OwnedRoot;
        public IDisposable? Transport;
        public CandidateWorkspace? Workspace;
        public ISandboxSession? Session;
        public bool ContainerCreationAttempted;
        public bool ContainerRemovalProven;
        public string? ContainerId;
        public SandboxAttemptOwnership? Ownership;
    }

    public async Task<CoderResult> ImplementAsync(
        CoderRunContext ctx, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            ctx.Validate();
            _config.Sandbox.ValidateLiveDocker();
            _lease.ThrowIfNotLiveFor(_git.RepoPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Failure("coder context, configuration or lock validation", ex);
        }

        RequireHostState(ctx.Candidate, expectedHead: ctx.Candidate.CommitSha);
        var state = new RunState();
        SandboxCommandResult? commandResult = null;
        Exception? primary = null;
        var cancelledByCaller = false;

        try
        {
            commandResult = await RunContainerAsync(ctx, state, ct);
            cancelledByCaller = ct.IsCancellationRequested || commandResult.Cancelled;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelledByCaller = true;
        }
        catch (Exception ex)
        {
            primary = ex;
        }

        var cleanupFailure = await StopAndRemoveAsync(state);
        if (cleanupFailure is not null)
        {
            DisposeTransport(state);
            throw new CoderInfrastructureException(
                "coder sandbox cleanup could not be proven at stage " + state.Stage +
                "; the attempt workspace is quarantined (" + cleanupFailure + ").");
        }

        if (ct.IsCancellationRequested && state.Workspace is null)
        {
            await DeleteWorkspaceAndDisposeTransportAsync(state);
            throw new OperationCanceledException(
                "the coder was cancelled before candidate materialization completed.", ct);
        }

        if (primary is not null)
        {
            var failedStage = state.Stage;
            await DeleteWorkspaceAndDisposeTransportAsync(state);
            if (primary is CoderInfrastructureException controlled) throw controlled;
            throw Failure(failedStage, primary);
        }

        if (!cancelledByCaller &&
            commandResult is not { Succeeded: true })
        {
            var result = CandidateFailure(
                commandResult ?? new SandboxCommandResult(
                    -1, "", "", TimedOut: false, Cancelled: false, OomKilled: false,
                    OutputTruncated: false, Duration: TimeSpan.Zero),
                ctx);
            await DeleteWorkspaceAndDisposeTransportAsync(state);
            return result;
        }

        CandidatePromotionResult promotion;
        try
        {
            state.Stage = "trusted candidate promotion";
            promotion = Promote(ctx, state, cancelledByCaller
                ? CancellationToken.None
                : ct);
            RequireHostState(
                ctx.Candidate,
                expectedHead: promotion.CommitSha ?? ctx.Candidate.CommitSha);
        }
        catch (CandidatePolicyRejectedException)
        {
            await DeleteWorkspaceAndDisposeTransportAsync(state);
            return new CoderResult
            {
                ProducedChanges = false,
                Summary = "coder output was rejected by the trusted promotion policy",
                FilesTouched = [],
            };
        }
        catch (Exception ex)
        {
            var failedStage = state.Stage;
            await DeleteWorkspaceAndDisposeTransportAsync(state);
            throw Failure(failedStage, ex);
        }

        var files = promotion.Patch?.Changes
            .Select(change => change.NormalizedPath)
            .Order(StringComparer.Ordinal)
            .ToList() ?? [];
        await DeleteWorkspaceAndDisposeTransportAsync(state);

        if (cancelledByCaller)
            throw new CoderCheckpointedCancellationException(promotion.CommitSha, ct);

        return new CoderResult
        {
            ProducedChanges = !promotion.NoChanges,
            CommitSha = promotion.CommitSha,
            Summary = promotion.NoChanges
                ? "coder completed without a promotable change"
                : $"containerized coder promoted {promotion.ChangedFileCount} validated files",
            FilesTouched = files,
        };
    }

    private async Task<SandboxCommandResult> RunContainerAsync(
        CoderRunContext ctx, RunState state, CancellationToken ct)
    {
        state.Stage = "managed workspace root preparation";
        state.ManagedRoot = PrepareManagedRoot(state);
        var transport = _transportFactory?.Invoke() ?? new DockerCliProcessTransport();
        state.Transport = transport as IDisposable
            ?? throw new CoderInfrastructureException(
                "the coder-owned Docker transport must be disposable.");
        var cli = new DockerCli(transport);
        var runtime = _runtimeFactory?.Invoke(cli, state.ManagedRoot)
            ?? new DockerCliSandboxRuntime(cli, _config.Sandbox, _git.RepoPath, state.ManagedRoot);
        var preflight = _preflightFactory?.Invoke(cli, state.ManagedRoot)
            ?? new DockerSandboxPreflight(
                cli, _config.Sandbox, state.ManagedRoot, _git.RepoPath,
                ownedManagedRoot: state.OwnedRoot is not null);

        state.Stage = "Docker preflight";
        var report = await preflight.RunAsync(ct);
        if (!report.IsReady)
            throw new CoderInfrastructureException(
                "Docker preflight did not pass; candidate code was not executed.");
        foreach (var warning in report.Warnings.Take(8))
            _log?.Invoke(Bound("coder preflight warning: " + Sanitize(warning)));

        state.Stage = "candidate materialization";
        var runId = "coder-" + Guid.NewGuid().ToString("N")[..12];
        var attemptId = ctx.Attempt.ToString(CultureInfo.InvariantCulture);
        var labels = Labels(
            runId, attemptId, ctx.WorkPackage.Id, "coder", ctx.Candidate.CommitSha);
        state.Ownership = new SandboxAttemptOwnership(
            _git.RepoPath, state.ManagedRoot, state.OwnedRoot is not null, labels);
        state.Workspace = new CandidateWorkspaceFactory(_git).Create(
            new CandidateWorkspaceRequest
            {
                CommitSha = ctx.Candidate.CommitSha,
                ManagedRoot = state.ManagedRoot,
                WorkBranch = ctx.Candidate.WorkBranch,
                MainBaseSha = ctx.Candidate.MainBaseSha,
                Role = SandboxRole.Coder,
                RunId = runId,
                AttemptId = attemptId,
                Limits = new MaterializationLimits
                {
                    MaxTotalBytes = checked((long)_config.Sandbox.MaxWorkspaceMb * 1024 * 1024),
                },
                AttemptCreated = state.Ownership.RecordAttempt,
            }, ct);

        state.Stage = "coder tool planning";
        var plan = CoderToolPlan.Create(_config, ctx);
        var workspace = state.Workspace;
        var role = _config.Sandbox.Roles.Coder;
        var spec = new SandboxSpec
        {
            Role = SandboxRole.Coder,
            Image = role.Image,
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                workspace.SourcePath, state.ManagedRoot, _git.RepoPath),
            Network = SandboxNetworkPolicy.Model,
            Cpus = role.Cpus,
            MemoryMb = role.MemoryMb,
            Pids = role.Pids,
            Timeout = TimeSpan.FromSeconds(role.TimeoutSeconds),
            CandidateSha = workspace.Revision.CommitSha,
            Labels = labels,
            Environment = plan.Environment
                .Append(new KeyValuePair<string, string>("TENNINETY_WP", ctx.WorkPackage.Id))
                .Append(new KeyValuePair<string, string>("TENNINETY_ATTEMPT",
                    ctx.Attempt.ToString(CultureInfo.InvariantCulture)))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        };
        spec.Validate();

        state.Stage = "coder container creation";
        state.ContainerCreationAttempted = true;
        state.Session = await runtime.CreateAsync(spec, ct);
        state.ContainerId = state.Session.Info.ContainerId;
        state.Ownership.SetContainer(state.ContainerId);

        state.Stage = "coder tool execution";
        var command = _coderCommandFactory?.Invoke(ctx)
            ?? plan.ToSandboxCommand(TimeSpan.FromSeconds(role.TimeoutSeconds));
        var result = await state.Session.RunAsync(command, ct);
        if (result.Cancelled && ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
        if (result.TimedOut || result.OomKilled || result.OutputTruncated || result.Cancelled ||
            result.SyntheticInfrastructureFailure)
            throw new CoderInfrastructureException(
                "the coder command ended without a complete definitive result " +
                $"(timeout={result.TimedOut}, cancelled={result.Cancelled}, " +
                $"oom={result.OomKilled}, truncated={result.OutputTruncated}, " +
                $"synthetic={result.SyntheticInfrastructureFailure}).");
        return result;
    }

    private CandidatePromotionResult Promote(
        CoderRunContext ctx, RunState state, CancellationToken ct)
    {
        var workspace = state.Workspace
            ?? throw new InvalidOperationException("the coder workspace is unavailable.");
        if (!state.ContainerRemovalProven)
            throw new InvalidOperationException(
                "the coder container was not proven removed before promotion.");
        var proof = QuiescenceProof.Issue(
            workspace.RunId, workspace.AttemptId, workspace.Role, workspace.AttemptRoot,
            "container removed:" + (state.ContainerId ?? "unknown"));
        var promotion = _config.Sandbox.Promotion;
        return new CandidatePromotionService(_git).PromoteValidated(
            workspace,
            proof,
            new CandidatePromotionOptions
            {
                Scan = new CandidateScanLimits
                {
                    MaxTotalBytes = checked((long)_config.Sandbox.MaxWorkspaceMb * 1024 * 1024),
                },
                Policy = new PromotionPolicyOptions
                {
                    MaxChangedFiles = promotion.MaxChangedFiles,
                    AllowSensitivePaths = promotion.AllowSensitivePaths,
                },
                MaxPatchBytes = checked((long)promotion.MaxPatchMb * 1024 * 1024),
            },
            new PromotionPreconditions(
                ctx.Candidate.WorkBranch,
                ctx.Candidate.CommitSha,
                ctx.Candidate.MainBaseSha,
                $"{ctx.WorkPackage.Id}: containerized coder [attempt {ctx.Attempt}]"),
            _lease,
            ct);
    }

    private async Task<string?> StopAndRemoveAsync(RunState state)
    {
        if (state.Session is null)
            return state.ContainerCreationAttempted
                ? "container creation was attempted without a returned removal capability"
                : null;

        state.Stage = "coder container cleanup";
        var failures = new List<string>();
        try { await SandboxCleanupDeadline.StopAsync(state.Session); }
        catch (Exception ex) { failures.Add("stop " + ex.GetType().Name); }
        try
        {
            await SandboxCleanupDeadline.DisposeAsync(state.Session);
            state.ContainerRemovalProven = true;
            state.Ownership?.ContainerRemoved();
        }
        catch (Exception ex)
        {
            failures.Add("removal " + ex.GetType().Name);
        }
        state.Session = null;
        if (state.ContainerRemovalProven && failures.Count > 0)
            _log?.Invoke(Bound(
                "coder container removal was proven but the stop step reported: " +
                string.Join(", ", failures)));
        return state.ContainerRemovalProven ? null : string.Join(", ", failures);
    }

    private async Task DeleteWorkspaceAndRootAsync(RunState state)
    {
        state.Stage = "coder workspace cleanup";
        if (state.Workspace is { } workspace)
        {
            try
            {
                await TrustedWorkspaceDeletion.DeleteAsync(
                    workspace.AttemptRoot, state.ManagedRoot!, _deleteWorkspaceOverride);
                state.Workspace = null;
            }
            catch (Exception ex)
            {
                throw Failure("coder workspace cleanup", ex);
            }
        }
        if (state.OwnedRoot is { } owned)
        {
            try
            {
                TrustedWorkspaceDeletion.DeleteEmptyOwnedDirectory(owned);
                state.OwnedRoot = null;
            }
            catch (Exception ex)
            {
                throw Failure("coder managed-root cleanup", ex);
            }
        }
        if (state.Workspace is null)
        {
            state.Ownership?.CompleteIfAttemptAbsent();
            state.Ownership = null;
        }
    }

    private async Task DeleteWorkspaceAndDisposeTransportAsync(RunState state)
    {
        Exception? deletionFailure = null;
        try { await DeleteWorkspaceAndRootAsync(state); }
        catch (Exception ex) { deletionFailure = ex; }
        var transportFailure = DisposeTransport(state);
        if (deletionFailure is not null) throw deletionFailure;
        if (transportFailure is not null)
            throw Failure("coder Docker transport cleanup", transportFailure);
    }

    private string PrepareManagedRoot(RunState state)
    {
        if (!string.IsNullOrWhiteSpace(_config.Sandbox.WorkspaceRoot))
        {
            var configured = ValidatedManagedRootPath.Create(
                _config.Sandbox.WorkspaceRoot).Value;
            EnsureSeparated(configured, _git.RepoPath);
            return configured;
        }

        var owned = Directory.CreateTempSubdirectory("tenninety-coder-root-").FullName;
        state.OwnedRoot = owned;
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(owned,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var validated = ValidatedManagedRootPath.Create(owned).Value;
        EnsureSeparated(validated, _git.RepoPath);
        state.OwnedRoot = validated;
        return validated;
    }

    private void RequireHostState(CandidateRevision candidate, string expectedHead)
    {
        try
        {
            if (!string.Equals(_git.CurrentBranch(), candidate.WorkBranch, StringComparison.Ordinal) ||
                !string.Equals(_git.HeadSha(), expectedHead, StringComparison.Ordinal) ||
                !string.Equals(_git.FindCommit(TenNinety.MainBranch)?.Sha,
                    candidate.MainBaseSha, StringComparison.Ordinal) ||
                !_git.IsClean())
                throw new CoderInfrastructureException(
                    "the authoritative repository no longer matches the trusted coder candidate.");
        }
        catch (CoderInfrastructureException) { throw; }
        catch (Exception ex) { throw Failure("authoritative repository verification", ex); }
    }

    private IReadOnlyDictionary<string, string> Labels(
        string runId, string attemptId, string workPackageId, string role,
        string candidateSha) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenninety.instance"] = "tenninety",
            ["tenninety.repository"] = SandboxPolicy.RepositoryIdentity(_git.RepoPath),
            ["tenninety.run"] = runId,
            ["tenninety.wp"] = workPackageId,
            ["tenninety.attempt"] = attemptId,
            ["tenninety.role"] = role,
            ["tenninety.candidate"] = candidateSha,
        };

    private static CoderResult CandidateFailure(
        SandboxCommandResult result, CoderRunContext ctx) => new()
    {
        ProducedChanges = false,
        Summary = $"containerized coder exited {result.ExitCode} for {ctx.WorkPackage.Id}",
        FilesTouched = [],
    };

    private static void EnsureSeparated(string root, string repository)
    {
        if (root == repository || root.StartsWith(repository + "/", StringComparison.Ordinal) ||
            repository.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the coder managed root overlaps the authoritative repository.");
    }

    private static Exception? DisposeTransport(RunState state)
    {
        try
        {
            state.Transport?.Dispose();
            return null;
        }
        catch (Exception ex) { return ex; }
        finally { state.Transport = null; }
    }

    private static CoderInfrastructureException Failure(string stage, Exception exception) =>
        new(Bound($"coder infrastructure failure at {stage} ({exception.GetType().Name})."),
            exception);

    private static string Sanitize(string value) =>
        Core.Security.Sanitizer.SanitizeText(value ?? "");

    private static string Bound(string value) =>
        value.Length <= 2000 ? value : value[..1990] + "...[bounded]";
}
