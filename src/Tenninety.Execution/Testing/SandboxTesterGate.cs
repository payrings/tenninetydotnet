using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;
using System.Xml;

namespace Tenninety.Execution.Testing;

/// <summary>
/// Trusted coordinator for optional restricted Restore and the offline Docker Tester: implements
/// <see cref="ITesterAgent"/> by running every invocation against a fresh disposable workspace
/// materialized from the exact trusted candidate commit inside a hardened, offline container.
///
/// Required order for every invocation:
///   1. validate the logical context and configuration;
///   2. validate any enabled, versioned operator Restore acceptance before resource acquisition;
///   3. verify the authoritative branch/HEAD/recorded main SHA/clean state against the
///      trusted candidate context (fail closed, no reset, no repair);
///   4. prepare the private managed root and production Docker dependencies;
///   5. run the REAL Docker preflight before any candidate code executes;
///   6. refuse failed or indeterminate preflight and surface its bounded warnings;
///   7. materialize the exact requested SHA with <see cref="CandidateWorkspaceFactory"/>;
///   8. apply MaxWorkspaceMb to the materialization limits with checked arithmetic;
///   9. optionally Restore in the accepted restricted network, remove that container, and
///      validate bounded derived output without following redirects;
///  10. discover a test project in the MATERIALIZED source (never the authoritative checkout);
///  11. construct a validated offline Tester <see cref="SandboxSpec"/>;
///  12. create the hardened Tester session;
///  13. execute build and test through the container-only <see cref="ShellTesterAgent"/>;
///  14. stop and dispose the session, proving container removal;
///  15. delete the owned attempt workspace safely;
///  16. recheck the authoritative host state;
///  17. return the final result only after cleanup and host-state verification succeed.
///
/// Failure classification (control flow, never message text): ordinary candidate build/test
/// failures — definitive nonzero exits, operational indeterminacies (timeout, OOM, output
/// truncation, exhausted command budget) and explicit zero-test outcomes — are returned as
/// regular failed <see cref="TestRunResult"/> values and keep the normal Coder retry path.
/// Infrastructure and refusal outcomes — configuration refusal, failed preflight, failed
/// materialization, failed container creation, session infrastructure exceptions, a SYNTHETIC
/// negative exit with no operational flag (startup/I/O failure), an indeterminate
/// infrastructure-layer command cancellation, host-state mismatch and unproven
/// cleanup/retention — throw <see cref="TesterInfrastructureException"/> so the engine's
/// existing infrastructure-exception path aborts the run: no automatic coding retry, no
/// promotion, no Frontier escalation.
///
/// Diagnostics contract: every public Tester exception, log line, audit entry and candidate
/// feedback string is constructed from CONTROLLED failure categories/stages and bounded
/// non-secret identifiers (validated run labels, container IDs, generated directory
/// basenames, and commit-SHA-shaped identifiers). Underlying exception messages, inner
/// exception chains, daemon output, raw invalid configuration values, raw branch strings and
/// raw git output are never copied into them — a sanitizer is defense in depth, not proof
/// that arbitrary text is safe. Message provenance is established by the explicit
/// <see cref="TesterInfrastructureException.Provenance"/> marker (never the CLR type alone):
/// only Tester-controlled instances are published verbatim; everything else — including an
/// untrusted instance of the same exception type — is reduced to the controlled stage/category
/// plus the exception type name. Exactly ONE final length bound
/// (<see cref="MaxPublicTesterMessageChars"/>) is applied to the COMPLETE public message
/// after all prefixes, categories, markers and retention information are assembled, and the
/// result never exceeds it.
///
/// Cancellation precedence: the caller's token is checked before any resource acquisition;
/// after cleanup succeeds, ACTUAL caller cancellation propagates as a SAFE, controlled
/// <see cref="OperationCanceledException"/> (a raw underlying cancellation exception is never
/// rethrown), while a `Cancelled=true` command result WITHOUT caller cancellation is an
/// indeterminate infrastructure failure. Cleanup itself runs independently of the cancelled
/// token; when cancellation and a cleanup failure coincide, both facts surface (retained
/// resources are never concealed behind a cancellation exception).
///
/// Cleanup semantics: the Tester never calls candidate scanning, patch building, promotion,
/// `git add` or commit APIs on its output; build artifacts, test mutations and the disposable
/// `.git` are discarded with the attempt workspace. FAILED workspaces are discarded too —
/// `KeepFailedWorkspaces` is deliberately NOT honored on this Tester path (a workspace that
/// cannot be safely cleaned up is an ERROR to be reported, never a retention mode). Cleanup
/// never follows a workspace-created symlink, never deletes the managed root or another
/// attempt, never relies on the caller's (possibly cancelled) token, and never lets a failure
/// turn into a pass. If container removal cannot be proven the workspace is conservatively
/// RETAINED and the unproven cleanup is reported. The automatically owned managed root is
/// removed only when it is proven safe and EMPTY (non-recursive deletion).
/// </summary>
public sealed class SandboxTesterGate : ITesterAgent
{
    private readonly IGitService _authoritativeGit;
    private readonly TenNinetyConfig _config;
    private readonly Action<string>? _log;
    private readonly Func<IDockerCliTransport>? _transportFactory;
    private readonly Func<DockerCli, string, ISandboxRuntime>? _runtimeFactory;
    private readonly Func<DockerCli, string, DockerSandboxPreflight>? _preflightFactory;
    private readonly Func<string, Task>? _deleteWorkspaceOverride;

    /// <summary>Production construction: real transport, runtime, preflight and deletion only.</summary>
    public SandboxTesterGate(IGitService authoritativeGit, TenNinetyConfig config, Action<string>? log = null)
        : this(authoritativeGit, config, log,
              transportFactory: null, runtimeFactory: null, preflightFactory: null,
              deleteWorkspaceOverride: null)
    {
    }

    /// <summary>Seam constructor (InternalsVisibleTo) for deterministic tests. Production
    /// construction MUST use the real-implementation constructor: every null seam resolves to
    /// the real implementation, and there is no "preflight passed" stub anywhere.</summary>
    internal SandboxTesterGate(
        IGitService authoritativeGit,
        TenNinetyConfig config,
        Action<string>? log,
        Func<IDockerCliTransport>? transportFactory,
        Func<DockerCli, string, ISandboxRuntime>? runtimeFactory,
        Func<DockerCli, string, DockerSandboxPreflight>? preflightFactory,
        Func<string, Task>? deleteWorkspaceOverride)
    {
        _authoritativeGit = authoritativeGit ?? throw new ArgumentNullException(nameof(authoritativeGit));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log;
        _transportFactory = transportFactory;
        _runtimeFactory = runtimeFactory;
        _preflightFactory = preflightFactory;
        _deleteWorkspaceOverride = deleteWorkspaceOverride;
    }

    private sealed class RunState
    {
        public string? ManagedRoot;
        public string? OwnedRoot;
        public IDisposable? Transport;
        public CandidateWorkspace? Workspace;
        public ISandboxSession? Session;
        public string? RunLabel;
        public bool SessionCreated;
        public bool SessionDisposalProven;
        public bool WorkspaceDeleted;
        public string? RetainedWorkspace;
        public string? RestoreOutputSha256;
        public SandboxAttemptOwnership? Ownership;

        /// <summary>The controlled stage label for the step currently executing. Public
        /// diagnostics report this stage (never arbitrary exception text) so primary
        /// failures keep a useful, non-secret distinction.</summary>
        public string Stage = "initialization";
    }

    /// <summary>Narrow internal test seam (InternalsVisibleTo; production never sets it):
    /// invoked inside <see cref="PrepareManagedRoot"/> AFTER the owned root directory exists
    /// and ownership has been recorded, but BEFORE the remaining initialization (owner-only
    /// file mode, validation) completes. It receives the EXACT newly created owned root
    /// path, so tests never identify the root by timestamps, directory scans or prefix
    /// matches. Tests inject deterministic failures here to prove that immediate ownership
    /// recording protects the cleanup for root-initialization failures.</summary>
    internal Action<string>? OwnedRootInitializationHook { get; set; }

    private sealed record CleanupEvidence(
        IReadOnlyList<string> Failures,
        string? RetainedWorkspace,
        bool WorkspaceDeleted)
    {
        public bool Proven => Failures.Count == 0;
    }

    public async Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default)
    {
        // Caller cancellation is checked before ANY resource acquisition or Docker work.
        ct.ThrowIfCancellationRequested();

        // ---- 1. logical context and configuration ------------------------------------
        // Early validation must never publish raw invalid input: the shared validators echo
        // hostile values (bounded) in their own messages, so their failures are reduced to
        // controlled categories here, exactly like any other unknown exception. Validation
        // itself is unchanged and still fails closed BEFORE any resource acquisition.
        try
        {
            ctx.Validate();
            _config.Sandbox.ValidateStructural();
        }
        catch (Exception ex)
        {
            throw PublicTesterFailure(
                "tester context or configuration validation failed (" + ex.GetType().Name +
                "); the run is refused before any resource acquisition.");
        }
        var sandbox = _config.Sandbox;
        var tester = sandbox.Roles.Tester;

        // ---- 2. validate the optional Restore acceptance BEFORE resource acquisition ----
        if (tester.Restore.Enabled)
            ValidateRestoreAcceptance(ctx, tester.Restore);

        // ---- 3. authoritative host state must match the trusted candidate context ------
        var hostMismatch = InspectHostState("initial", ctx.Candidate);
        if (hostMismatch is not null)
            throw PublicTesterFailure(
                "the authoritative host state does not match the trusted candidate context; " +
                "the tester refuses to run without repair: " + hostMismatch);

        var state = new RunState();
        TestRunResult? outcome = null;
        Exception? primary = null;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(TimeSpan.FromSeconds(tester.TimeoutSeconds));
        try
        {
            outcome = await RunCoreAsync(ctx, sandbox, state, overall.Token);
        }
        catch (OperationCanceledException ex)
        {
            primary = ex;
        }
        catch (Exception ex)
        {
            primary = ex;
        }

        // ---- 13/14. cleanup outside caller cancellation --------------------------------
        var cleanup = await CleanupAfterRunAsync(state);

        // Cancellation precedence: actual caller cancellation propagates after proven
        // cleanup; with an unproven cleanup BOTH facts surface (retained resources must
        // never be concealed behind a cancellation exception). The propagated exception is
        // always a SAFE, controlled caller-cancellation exception — a raw underlying
        // cancellation exception (arbitrary message or inner chain) is never rethrown.
        if (ct.IsCancellationRequested)
        {
            if (cleanup.Proven)
            {
                throw new OperationCanceledException(
                    "the tester run was cancelled by the caller before it could complete.", ct);
            }
            throw PublicTesterFailure(
                "the tester run was cancelled by the caller AND cleanup could not be fully " +
                "proven; retained scoped resources must be removed by an operator" +
                (primary is null ? "" : "; primary failure: " + Describe(primary, state)) +
                ". cleanup evidence: " + DescribeCleanup(cleanup));
        }

        if (!cleanup.Proven)
            throw PublicTesterFailure(
                "the tester cleanup could not be fully proven; the run is a failure regardless " +
                "of any test outcome and no automatic retry may follow" +
                (primary is null ? "" : "; primary failure: " + Describe(primary, state)) +
                ". cleanup evidence: " + DescribeCleanup(cleanup));

        if (primary is not null)
        {
            // Provenance, never the CLR type: a lower layer or injected session can throw
            // this same exception type with arbitrary text through the public constructor.
            // Only provenance-marked (Tester-controlled) instances are published verbatim;
            // everything else — including the untrusted typed instance — is reduced to the
            // controlled stage/category composition.
            if (primary is TesterInfrastructureException { Provenance: TesterInfrastructureProvenance.Controlled } trusted)
                throw trusted;
            throw PublicTesterFailure(
                "the tester run failed before it could produce a verdict: " +
                Describe(primary, state));
        }

        // ---- 15. host state recheck ------------------------------------------------------
        var hostChanged = InspectHostState("final", ctx.Candidate);
        if (hostChanged is not null)
            throw PublicTesterFailure(
                "the authoritative host state changed while the tester ran; refusing the " +
                "gate without repair: " + hostChanged);

        // ---- 16. final result (identity bound to the materialized revision) --------------
        return outcome!;
    }

    /// <summary>Steps 4–12: production dependencies, preflight, materialization, discovery,
    /// spec, session, and the container-only build/test run.</summary>
    private async Task<TestRunResult> RunCoreAsync(
        TesterRunContext ctx, SandboxConfig sandbox, RunState state, CancellationToken ct)
    {
        var tester = sandbox.Roles.Tester;

        // ---- 4. managed root + production dependencies (built lazily, owned per run) ----
        ct.ThrowIfCancellationRequested();
        state.ManagedRoot = PrepareManagedRoot(state);
        var transport = _transportFactory?.Invoke() ?? new DockerCliProcessTransport();
        state.Transport = transport as IDisposable
            ?? throw PublicTesterFailure(
                "the tester-owned docker transport must be disposable so its owned work can " +
                "be finished deterministically.");
        var cli = new DockerCli(transport);
        var runtime = _runtimeFactory?.Invoke(cli, state.ManagedRoot)
            ?? new DockerCliSandboxRuntime(cli, sandbox, _authoritativeGit.RepoPath, state.ManagedRoot);
        var preflight = _preflightFactory?.Invoke(cli, state.ManagedRoot)
            ?? new DockerSandboxPreflight(
                cli, sandbox, state.ManagedRoot, _authoritativeGit.RepoPath,
                ownedManagedRoot: state.OwnedRoot is not null);

        // ---- 5/6. real preflight before any candidate code runs --------------------------
        state.Stage = "preflight";
        var report = await preflight.RunAsync(ct);
        if (!report.IsReady)
            throw PublicTesterFailure(
                "docker preflight did not pass; refusing to execute candidate code. errors: " +
                string.Join("; ", report.Errors.Take(8).Select(Sanitize)) +
                (report.Warnings.Count > 0
                    ? " warnings: " + string.Join("; ", report.Warnings.Take(8).Select(Sanitize))
                    : ""));

        // Reduced-protection warnings must not silently disappear just because the
        // preflight is ready: surface them before any candidate code executes. The COMPLETE
        // log line (prefix + warning) is assembled first and bounded LAST.
        foreach (var warning in report.Warnings.Take(8))
            _log?.Invoke(FinalPublicBound(
                "tester preflight warning (reduced protection): " + Sanitize(warning)));

        // ---- 7/8. exact candidate materialization with checked limits --------------------
        state.Stage = "materialization";
        var limits = new MaterializationLimits
        {
            MaxTotalBytes = checked((long)sandbox.MaxWorkspaceMb * 1024 * 1024),
        };
        limits.Validate();
        var factory = new CandidateWorkspaceFactory(_authoritativeGit);
        var runId = "tester-" + Guid.NewGuid().ToString("N")[..12];
        state.RunLabel = runId;
        var journalLabels = AttemptLabels(
            runId, ctx, "tester", ctx.Candidate.CommitSha);
        state.Ownership = new SandboxAttemptOwnership(
            _authoritativeGit.RepoPath,
            state.ManagedRoot,
            state.OwnedRoot is not null,
            journalLabels);
        CandidateWorkspace workspace;
        try
        {
            workspace = factory.Create(new CandidateWorkspaceRequest
            {
                CommitSha = ctx.Candidate.CommitSha,
                ManagedRoot = state.ManagedRoot,
                WorkBranch = ctx.Candidate.WorkBranch,
                MainBaseSha = ctx.Candidate.MainBaseSha,
                Role = SandboxRole.Tester,
                RunId = runId,
                AttemptId = ctx.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Limits = limits,
                AttemptCreated = state.Ownership.RecordAttempt,
            }, ct);
        }
        catch (Exception ex)
        {
            throw PublicTesterFailure(
                "candidate materialization failed (" + ex.GetType().Name + "); the run is " +
                "refused and no container is started.");
        }
        state.Workspace = workspace;
        if (!string.Equals(workspace.Revision.CommitSha, ctx.Candidate.CommitSha,
                StringComparison.Ordinal))
            throw PublicTesterFailure(
                "the materialized workspace revision does not match the requested candidate " +
                "SHA; the run is refused and the workspace is discarded.");

        // ---- 9. optional accepted restricted Restore --------------------------------------
        if (tester.Restore.Enabled)
        {
            var restoreFailure = await RunRestoreAsync(
                runtime, workspace, ctx, sandbox, state, ct);
            if (restoreFailure is not null)
                return GateFailure(ctx, restoreFailure);
        }

        // ---- 10. discovery in the MATERIALIZED source only -------------------------------
        state.Stage = "test-project-discovery";
        var testProject = TestProjectDiscovery.FindTestProject(workspace.SourcePath);
        if (testProject is null)
            return GateFailure(ctx,
                "no test project found in the materialized candidate workspace " +
                "(a csproj referencing xunit/nunit/mstest or marked IsTestProject) - failing " +
                "closed: an application-only candidate runs zero tests and cannot gate a promotion.");

        // ---- 11. validated Tester spec -----------------------------------------------------
        state.Stage = "tester-specification";
        var spec = BuildTesterSpec(sandbox, workspace, ctx, runId, state.ManagedRoot);
        spec.Validate();

        // ---- 12. hardened Tester session ----------------------------------------------------
        state.Stage = "container-creation";
        state.SessionDisposalProven = false;
        state.SessionCreated = true;
        ISandboxSession session;
        try
        {
            session = await runtime.CreateAsync(spec, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A container was ATTEMPTED without a returned session or removal proof: the
            // workspace is conservatively retained by cleanup (never deleted while a
            // container may still be writing it). Provenance rule: even a TesterInfrastructureException
            // from the runtime seam is untrusted here — controlled category + exception type only.
            throw PublicTesterFailure(
                "the tester container could not be created and proven running; the run is " +
                "refused and the attempt workspace is retained (" + ex.GetType().Name + ").");
        }
        state.Session = session;
        state.Ownership.SetContainer(session.Info.ContainerId);

        // ---- 13. container-only build/test run ----------------------------------------------
        state.Stage = "build-and-test-execution";
        var shellAgent = new ShellTesterAgent(
            _config.BuildCommand,
            _config.TestCommand,
            TimeSpan.FromSeconds(tester.TimeoutSeconds),
            spec.Timeout);
        TestRunResult raw;
        try
        {
            raw = await shellAgent.RunAsync(session, ctx, ct);
        }
        catch (TesterInfrastructureException trusted)
            when (trusted.Provenance == TesterInfrastructureProvenance.Controlled)
        {
            throw; // the Tester's own controlled infrastructure failure
        }
        catch (TesterInfrastructureException)
        {
            // A lower layer or injected session threw the SAME exception type through its
            // public constructor with arbitrary text: the CLR type proves nothing. It is
            // reduced to the controlled category exactly like any other unknown exception.
            throw PublicTesterFailure(
                "the tester command execution failed with a session infrastructure error " +
                "(TesterInfrastructureException); the result is indeterminate.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // actual caller cancellation propagates (safely) after cleanup
        }
        catch (Exception ex)
        {
            throw PublicTesterFailure(
                "the tester command execution failed with a session infrastructure error (" +
                ex.GetType().Name + "); the result is indeterminate.");
        }

        // The result identity comes from the trusted materialized revision, never from
        // command output or the disposable .git.
        return new TestRunResult
        {
            Passed = raw.Passed,
            ExitCode = raw.ExitCode,
            Command = raw.Command,
            OutputTail = raw.OutputTail,
            CandidateSha = workspace.Revision.CommitSha,
            RestoreOutputSha256 = state.RestoreOutputSha256,
        };
    }

    // ---- optional restricted Restore ---------------------------------------------------------

    private async Task<string?> RunRestoreAsync(
        ISandboxRuntime runtime,
        CandidateWorkspace workspace,
        TesterRunContext ctx,
        SandboxConfig sandbox,
        RunState state,
        CancellationToken ct)
    {
        var restore = sandbox.Roles.Tester.Restore;
        var validator = new RestoreIntegrityValidator();
        var workspaceLimit = checked((long)sandbox.MaxWorkspaceMb * 1024 * 1024);

        state.Stage = "restore-baseline-manifest";
        var baseline = validator.CaptureBaseline(
            workspace.SourcePath,
            workspaceLimit,
            maxFiles: 1_000_000,
            restore.MaxDerivedDepth,
            ct);

        state.Stage = "restore-control-preparation";
        var controlConfig = CreateRestoreControl(workspace.SourcePath, restore);
        var control = validator.CaptureTrustedControl(
            baseline,
            checked(workspaceLimit + 1_048_576),
            maxFiles: 1_000_008,
            restore.MaxDerivedDepth,
            ct);

        state.Stage = "restore-specification";
        var tester = sandbox.Roles.Tester;
        var spec = new SandboxSpec
        {
            Role = SandboxRole.Restore,
            Image = tester.Image,
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                workspace.SourcePath, state.ManagedRoot!, _authoritativeGit.RepoPath),
            Network = SandboxNetworkPolicy.Restore,
            Cpus = tester.Cpus,
            MemoryMb = tester.MemoryMb,
            Pids = tester.Pids,
            Timeout = TimeSpan.FromSeconds(restore.TimeoutSeconds),
            CandidateSha = workspace.Revision.CommitSha,
            Labels = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tenninety.instance"] = "tenninety",
                ["tenninety.repository"] = RepositoryIdentity(_authoritativeGit.RepoPath),
                ["tenninety.run"] = workspace.RunId,
                ["tenninety.wp"] = ctx.WorkPackageId,
                ["tenninety.attempt"] = ctx.Attempt.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["tenninety.role"] = "restore",
                ["tenninety.candidate"] = workspace.Revision.CommitSha,
            },
            Environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TENNINETY_WP"] = ctx.WorkPackageId,
                ["TENNINETY_ATTEMPT"] = ctx.Attempt.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                ["DOTNET_NOLOGO"] = "1",
                ["HTTP_PROXY"] = restore.ProxyUrl,
                ["HTTPS_PROXY"] = restore.ProxyUrl,
                ["NO_PROXY"] = "",
            },
        };
        spec.Validate();

        state.Stage = "restore-container-creation";
        state.SessionDisposalProven = false;
        state.SessionCreated = true;
        state.Session = await runtime.CreateAsync(spec, ct);
        state.Ownership?.SetContainer(state.Session.Info.ContainerId);

        state.Stage = "restricted-restore-execution";
        var result = await state.Session.RunAsync(new SandboxCommand
        {
            Executable = "/usr/bin/dotnet",
            Arguments =
            [
                "restore",
                "--locked-mode",
                "--configfile", controlConfig,
                "--packages", "/workspace/.tenninety/restore-packages",
                "--nologo",
            ],
            WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
            Timeout = TimeSpan.FromSeconds(restore.TimeoutSeconds),
            MaxOutputBytes = 4L * 1024 * 1024,
        }, ct);

        state.Stage = "restore-container-removal";
        var removalFailure = await RemoveCurrentSessionAsync(state);
        if (removalFailure is not null)
            throw PublicTesterFailure(
                "the Restore container could not be proven removed before integrity " +
                "validation; the workspace is retained: " + removalFailure);

        if (result.TimedOut || result.Cancelled || result.OomKilled ||
            result.OutputTruncated || result.SyntheticInfrastructureFailure)
            throw PublicTesterFailure(
                "Restore ended without a complete definitive result; no Tester container is started.");
        if (result.ExitCode != 0)
            return $"restricted Restore exited {result.ExitCode}; the candidate cannot be tested";

        state.Stage = "post-restore-integrity-validation";
        var maxDerivedBytes = checked((long)restore.MaxDerivedMb * 1024 * 1024);
        var verified = validator.VerifyPostRestore(
            baseline,
            control,
            new RestoreIntegrityLimits(
                restore.MaxDerivedFiles,
                checked((long)restore.MaxDerivedFileMb * 1024 * 1024),
                maxDerivedBytes,
                Math.Min(maxDerivedBytes, restore.Acceptance.StorageQuotaBytes),
                restore.MaxDerivedDepth),
            ct);
        state.RestoreOutputSha256 = verified.DerivedOutputSha256;
        _log?.Invoke(FinalPublicBound(
            "Restore integrity accepted derived output digest " +
            verified.DerivedOutputSha256[..12] +
            $" ({verified.DerivedFiles} files, {verified.DerivedLogicalBytes} logical bytes)."));
        return null;
    }

    private static string CreateRestoreControl(
        string workspaceRoot, SandboxRestoreConfig restore)
    {
        var packages = Path.Combine(workspaceRoot, ".tenninety", "restore-packages");
        var control = Path.Combine(workspaceRoot, ".tenninety", "restore-control");
        if (Directory.Exists(packages) || File.Exists(packages) ||
            Directory.Exists(control) || File.Exists(control))
            throw new InvalidOperationException(
                "the candidate collides with a reserved Restore package/control root.");

        Directory.CreateDirectory(control);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var tenninetyDirectory = Path.Combine(workspaceRoot, ".tenninety");
            File.SetUnixFileMode(tenninetyDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(control,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var hostConfig = Path.Combine(control, "NuGet.Config");
        var settings = new XmlWriterSettings
        {
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false,
            OmitXmlDeclaration = false,
        };
        using (var stream = new FileStream(
                   hostConfig, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var xml = XmlWriter.Create(stream, settings))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("configuration");
            xml.WriteStartElement("packageSources");
            xml.WriteStartElement("clear");
            xml.WriteEndElement();
            for (var i = 0; i < restore.ApprovedFeeds.Count; i++)
            {
                xml.WriteStartElement("add");
                xml.WriteAttributeString("key", "approved-" + (i + 1));
                xml.WriteAttributeString("value", new Uri(restore.ApprovedFeeds[i]).AbsoluteUri);
                xml.WriteAttributeString("protocolVersion", "3");
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(hostConfig,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return "/workspace/.tenninety/restore-control/NuGet.Config";
    }

    private static async Task<string?> RemoveCurrentSessionAsync(RunState state)
    {
        if (state.Session is null)
            return "no typed session was returned after container creation was attempted";
        var failures = new List<string>();
        try { await SandboxCleanupDeadline.StopAsync(state.Session); }
        catch (Exception ex) { failures.Add("stop failed (" + ex.GetType().Name + ")"); }
        try
        {
            await SandboxCleanupDeadline.DisposeAsync(state.Session);
            state.SessionDisposalProven = true;
            state.Session = null;
            state.Ownership?.ContainerRemoved();
        }
        catch (Exception ex)
        {
            failures.Add("removal failed (" + ex.GetType().Name + ")");
        }
        return state.SessionDisposalProven ? null : string.Join("; ", failures);
    }

    private void ValidateRestoreAcceptance(
        TesterRunContext ctx, SandboxRestoreConfig restore)
    {
        var acceptance = restore.Acceptance;
        if (!string.Equals(
                acceptance.Repository,
                RepositoryIdentity(_authoritativeGit.RepoPath),
                StringComparison.Ordinal) ||
            !string.Equals(acceptance.Instance, "tenninety", StringComparison.Ordinal))
            throw PublicTesterFailure(
                "Restore acceptance is not scoped to this repository and Tenninety instance.");
        if (!DateTimeOffset.TryParseExact(
                acceptance.ExpiresUtc,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var expires) ||
            expires.Offset != TimeSpan.Zero ||
            expires <= DateTimeOffset.UtcNow)
            throw PublicTesterFailure(
                "Restore acceptance is expired, non-UTC or malformed; no resource is acquired.");
        if (restore.TimeoutSeconds > _config.Sandbox.Roles.Tester.TimeoutSeconds)
            throw PublicTesterFailure(
                "Restore timeout exceeds the overall Tester attempt timeout.");
        _ = ctx; // context validation already established the exact candidate scope.
    }

    // ---- cleanup -----------------------------------------------------------------------------

    private async Task<CleanupEvidence> CleanupAfterRunAsync(RunState state)
    {
        var failures = new List<string>();

        // 13. stop and dispose the session; DisposeAsync proves removal or throws. A stop
        // failure never prevents the subsequent removal attempt; both failures are collected.
        // Failure entries are CONTROLLED compositions (stage + exception type + bounded
        // non-secret identifiers): underlying exception messages/inner chains are never copied.
        string? containerId = null;
        if (state.Session is { } session)
        {
            containerId = session.Info.ContainerId;
            try
            {
                await SandboxCleanupDeadline.StopAsync(session);
            }
            catch (Exception ex)
            {
                failures.Add("container stop: failed (" + ex.GetType().Name + ")");
            }
            try
            {
                await SandboxCleanupDeadline.DisposeAsync(session);
                state.SessionDisposalProven = true;
                state.Ownership?.ContainerRemoved();
            }
            catch (Exception ex)
            {
                failures.Add("container removal could not be proven (" + ex.GetType().Name + ")");
            }
            state.Session = null;
        }

        // 14. delete the owned attempt workspace — never while a container may be writing it.
        if (state.Workspace is { } workspace)
        {
            if (state.SessionCreated && !state.SessionDisposalProven)
            {
                state.RetainedWorkspace = workspace.AttemptRoot;
                failures.Add("the attempt workspace '" + Path.GetFileName(workspace.AttemptRoot) +
                             "' (run " + (state.RunLabel ?? "unknown") +
                             (containerId is null ? "" : ", container " + containerId) +
                             ") is conservatively RETAINED because container removal could not " +
                             "be proven: a container may still be writing it; the scoped " +
                             "resource must be removed by an operator");
            }
            else
            {
                try
                {
                    await DeleteWorkspaceAsync(workspace.AttemptRoot, state.ManagedRoot!);
                    state.WorkspaceDeleted = true;
                }
                catch (Exception ex)
                {
                    // A failed deletion is RETENTION, never a broader cleanup: the parent
                    // (owned root or configured root) must not be touched afterwards.
                    state.RetainedWorkspace = workspace.AttemptRoot;
                    failures.Add("workspace deletion failed; the attempt workspace '" +
                                 Path.GetFileName(workspace.AttemptRoot) +
                                 "' is retained (" + ex.GetType().Name + ")");
                }
            }
        }

        // Transport ownership: production transports are disposed once their owned work ends.
        if (state.Transport is { } transport)
        {
            try { transport.Dispose(); }
            catch (Exception ex)
            {
                failures.Add("docker transport disposal failed (" + ex.GetType().Name + ")");
            }
            state.Transport = null;
        }

        // The owned default root is removed only when it is proven safe and EMPTY, with a
        // non-recursive deletion. A retained workspace or unexpected contents keep it
        // (reported) — a failed workspace deletion is never followed by a broader recursive
        // deletion of its parent.
        if (state.OwnedRoot is { } owned)
        {
            if (state.RetainedWorkspace is not null)
            {
                failures.Add("the owned managed root (containing retained attempt '" +
                             Path.GetFileName(state.RetainedWorkspace) + "') is retained");
            }
            else
            {
                try
                {
                    TrustedWorkspaceDeletion.DeleteEmptyOwnedDirectory(owned);
                }
                catch (Exception ex)
                {
                    failures.Add("the owned managed root '" + Path.GetFileName(owned) +
                                 "' is retained (" + ex.GetType().Name + ")");
                }
            }
        }

        if (state.RetainedWorkspace is null)
        {
            try
            {
                state.Ownership?.CompleteIfAttemptAbsent();
                state.Ownership = null;
            }
            catch (Exception ex)
            {
                failures.Add("sandbox resource journal completion failed (" +
                             ex.GetType().Name + ")");
            }
        }

        return new CleanupEvidence(failures, state.RetainedWorkspace, state.WorkspaceDeleted);
    }

    /// <summary>Bounded, safe attempt-workspace deletion: strict child of the managed root,
    /// the managed-root chain revalidated immediately before deletion, no symlink components
    /// along the path, refuses to delete the managed root itself or any other attempt, and
    /// positively verifies absence afterwards.</summary>
    private async Task DeleteWorkspaceAsync(string attemptRoot, string managedRoot)
    {
        await TrustedWorkspaceDeletion.DeleteAsync(attemptRoot, managedRoot, _deleteWorkspaceOverride);
    }

    /// <summary>Kept as the shared validated deletion entry point (also used directly by the
    /// test seams and the preflight probe cleanup).</summary>
    internal static void DeleteAttemptDirectory(string attemptRoot, string managedRoot) =>
        TrustedWorkspaceDeletion.DeleteManagedChildDirectory(attemptRoot, managedRoot);

    // ---- helpers ------------------------------------------------------------------------------

    private string PrepareManagedRoot(RunState state)
    {
        var configured = _config.Sandbox.WorkspaceRoot;
        var repository = _authoritativeGit.RepoPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // The configured root must already exist; it is never created, replaced, chmod'ed
            // or deleted by the Tester.
            var root = ValidatedManagedRootPath.Create(configured).Value;
            EnsureRootRepositorySeparation(root, repository);
            return root;
        }

        // Unset root: a fresh, dedicated, owner-only private root beneath the system temp
        // directory. The system temporary directory itself is never the managed root.
        // Ownership is recorded IMMEDIATELY after creation so that any later initialization
        // failure (the test seam below, chmod, validation) still reaches the cleanup: a
        // proven-owned EMPTY directory is safely removed there; anything unexpected is
        // retained and reported.
        var owned = Directory.CreateTempSubdirectory("tenninety-tester-root-").FullName;
        state.OwnedRoot = owned;
        // Narrow internal test seam: invoked after the owned directory exists and ownership
        // is recorded, but BEFORE the remaining root initialization (owner-only file mode,
        // validation) completes. It receives the EXACT newly created owned root path.
        // Production never sets it; deterministic tests use it to prove that initialization
        // failures at this exact point still reach the cleanup with ownership already
        // recorded.
        if (OwnedRootInitializationHook is { } hook)
            hook(owned);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(owned,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var validated = ValidatedManagedRootPath.Create(owned).Value;
        EnsureRootRepositorySeparation(validated, repository);
        state.OwnedRoot = validated;
        return validated;
    }

    private static void EnsureRootRepositorySeparation(string root, string repository)
    {
        if (root == repository || root.StartsWith(repository + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the tester managed root must never be inside the authoritative repository.");
        if (repository.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the tester managed root must never contain the authoritative repository.");
    }

    private SandboxSpec BuildTesterSpec(
        SandboxConfig sandbox, CandidateWorkspace workspace, TesterRunContext ctx,
        string runId, string managedRoot)
    {
        var tester = sandbox.Roles.Tester;
        var candidateSha = workspace.Revision.CommitSha;
        return new SandboxSpec
        {
            Role = SandboxRole.Tester,
            Image = tester.Image,
            HostWorkspacePath = ValidatedSandboxWorkspacePath.Create(
                workspace.SourcePath, managedRoot, _authoritativeGit.RepoPath),
            Network = SandboxNetworkPolicy.None,
            Cpus = tester.Cpus,
            MemoryMb = tester.MemoryMb,
            Pids = tester.Pids,
            Timeout = TimeSpan.FromSeconds(tester.TimeoutSeconds),
            Labels = AttemptLabels(runId, ctx, "tester", candidateSha),
            CandidateSha = candidateSha,
            Environment = new Dictionary<string, string>
            {
                ["TENNINETY_WP"] = ctx.WorkPackageId,
                ["TENNINETY_ATTEMPT"] = ctx.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        };
    }

    private IReadOnlyDictionary<string, string> AttemptLabels(
        string runId, TesterRunContext ctx, string role, string candidateSha) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenninety.instance"] = "tenninety",
            ["tenninety.repository"] = RepositoryIdentity(_authoritativeGit.RepoPath),
            ["tenninety.run"] = runId,
            ["tenninety.wp"] = ctx.WorkPackageId,
            ["tenninety.attempt"] = ctx.Attempt.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["tenninety.role"] = role,
            ["tenninety.candidate"] = candidateSha,
        };

    /// <summary>Non-secret repository identity for management labels: never a raw host path.</summary>
    internal static string RepositoryIdentity(string repositoryPath)
        => SandboxPolicy.RepositoryIdentity(repositoryPath);

    /// <summary>
    /// Controlled host-state inspection for the INITIAL and FINAL checks. Both facts are
    /// published only as controlled categories: branch strings (which come from git output
    /// or the trusted context and are unrestricted text) are NEVER echoed, and only
    /// commit-SHA-shaped, length-bounded identifiers appear (unexpected HEAD shapes are
    /// described by category). Any inspection failure (git exceptions with arbitrary
    /// messages) is itself a controlled infrastructure failure — never raw git output.
    /// The check itself is unchanged: same comparisons, same fail-closed verdict.
    /// Returns null when the host state matches.
    /// </summary>
    private string? InspectHostState(string when, CandidateRevision candidate)
    {
        try
        {
            var currentBranch = _authoritativeGit.CurrentBranch();
            if (!string.Equals(currentBranch, candidate.WorkBranch, StringComparison.Ordinal))
                return "the authoritative checkout is on a different branch than the branch " +
                       "the candidate was recorded on (branch text withheld).";
            var head = _authoritativeGit.HeadSha();
            var headCategory = ShaPrefixCategory(head);
            if (headCategory is null)
                return "the authoritative HEAD is not a well-formed commit identifier; " +
                       "refusing the gate without repair.";
            if (!string.Equals(head, candidate.CommitSha, StringComparison.Ordinal))
                return $"the authoritative HEAD {headCategory} does not match the requested " +
                       $"candidate {BoundedSha(candidate.CommitSha)}.";
            var main = _authoritativeGit.FindCommit(TenNinety.MainBranch)?.Sha;
            if (!string.Equals(main, candidate.MainBaseSha, StringComparison.Ordinal))
                return "the authoritative main tip does not match the recorded candidate base.";
            if (!_authoritativeGit.IsClean())
                return "the authoritative working tree is not clean.";
            return null;
        }
        catch (Exception ex)
        {
            // Git inspection failures carry arbitrary messages (paths, daemon-ish output):
            // reduce to the controlled category plus the exception type name.
            throw PublicTesterFailure(
                $"the {when} authoritative host-state inspection failed " +
                "(" + ex.GetType().Name + "); the tester refuses to run without repair.");
        }
    }

    /// <summary>12-hex prefix of a well-formed commit SHA, or null when the value is not a
    /// well-formed commit identifier (then it is described by category, never echoed).</summary>
    private static string? ShaPrefixCategory(string value) =>
        TesterRunContext.IsFullCommitSha(value) ? value[..12] : null;

    private static string BoundedSha(string candidateSha) =>
        TesterRunContext.IsFullCommitSha(candidateSha)
            ? candidateSha[..12]
            : "<invalid candidate identity>";

    private static TestRunResult GateFailure(TesterRunContext ctx, string reason) =>
        new()
        {
            Passed = false,
            ExitCode = -1,
            Command = "(tester-gate)",
            OutputTail = FinalPublicBound(Sanitize(reason)),
            CandidateSha = ctx.Candidate.CommitSha,
        };

    /// <summary>Bounded, safe description of an unexpected primary failure. Infrastructure
    /// diagnostics are constructed from CONTROLLED failure categories, established by the
    /// explicit <see cref="TesterInfrastructureException.Provenance"/> marker — never by the
    /// CLR type alone (a lower layer or injected session can throw the same type with
    /// arbitrary text through the public constructor) and never by string matching. A
    /// provenance-marked instance carries the Tester's own controlled composition
    /// (re-sanitized as defense in depth, never an inner chain); every other exception —
    /// including an untrusted typed instance — is reduced to the controlled stage label and
    /// its exception TYPE name, both bounded, non-secret identifiers. Arbitrary exception
    /// messages, inner exception chains and daemon output are deliberately never copied:
    /// a sanitizer is defense in depth, not proof that arbitrary text is safe to publish.</summary>
    private static string Describe(Exception primary, RunState? state) =>
        primary is TesterInfrastructureException
            {
                Provenance: TesterInfrastructureProvenance.Controlled,
            }
            ? Sanitize(primary.Message)
            : "stage " + (state?.Stage ?? "unknown") + " failed (" + primary.GetType().Name + ")";

    /// <summary>Bounded, sanitized assembly of the cleanup failure categories and the bounded
    /// non-secret retained-resource identifiers. The COMPLETE public message is length-bound
    /// LAST, in <see cref="PublicTesterFailure"/>.</summary>
    private static string DescribeCleanup(CleanupEvidence cleanup) =>
        Sanitize(string.Join("; ", cleanup.Failures.Take(8))) +
        (cleanup.RetainedWorkspace is null
            ? ""
            : " retained: attempt '" + Path.GetFileName(cleanup.RetainedWorkspace) + "'");

    private static string Sanitize(string value) => Core.Security.Sanitizer.SanitizeText(value ?? "");

    /// <summary>
    /// The complete-message limit for EVERY public Tester diagnostic: exception messages,
    /// candidate-feedback tails and log lines handed to the logger. The bound applies to the
    /// fully assembled text — prefixes, suffixes, truncation markers and retention
    /// information included — and is applied LAST, after complete assembly. This contract
    /// covers exactly the string the Tester produces; an external log sink may add its own
    /// unrelated formatting, which is not claimed here.
    ///
    /// When the assembled text exceeds the limit, the middle is elided under a fixed marker
    /// and the result is EXACTLY <see cref="MaxPublicTesterMessageChars"/> characters (the
    /// marker's space is reserved first). Both ends are kept on purpose: the head carries
    /// the primary failure category and the tail carries the retained-resource evidence, so
    /// the most important categories survive truncation.
    /// </summary>
    public const int MaxPublicTesterMessageChars = 4000;

    private const string TruncationMarker = "…[bounded]";

    /// <summary>Final complete-message bound: at most
    /// <see cref="MaxPublicTesterMessageChars"/> characters including the truncation marker,
    /// applied after complete assembly. When truncation is needed, the assembled head (the
    /// primary failure categories) and tail (retention/cleanup evidence) are both preserved
    /// under the fixed marker, so the marker + retained information never push the message
    /// past the limit.</summary>
    internal static string FinalPublicBound(string message)
    {
        if (message.Length <= MaxPublicTesterMessageChars) return message;
        var keep = MaxPublicTesterMessageChars - TruncationMarker.Length;
        var headLen = keep - keep / 4;
        var tailLen = keep - headLen;
        return message[..headLen] + TruncationMarker + message[^tailLen..];
    }

    /// <summary>Creates the typed public Tester failure: provenance-marked (controlled)
    /// with the ONE final complete-message bound applied to the fully assembled text.</summary>
    private static TesterInfrastructureException PublicTesterFailure(string message) =>
        TesterInfrastructureException.Controlled(FinalPublicBound(message));
}
