using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Execution.Candidates;

/// <summary>Everything the factory needs to materialize one candidate workspace.</summary>
public sealed record CandidateWorkspaceRequest
{
    /// <summary>Full 40-hex commit SHA of the candidate (resolved/owned by trusted code).</summary>
    public required string CommitSha { get; init; }

    /// <summary>Canonical Tenninety-managed sandbox root; validated in full BEFORE any
    /// filesystem state is created, then used as the physical root for all construction.</summary>
    public required string ManagedRoot { get; init; }

    /// <summary>Work-branch identity metadata for the revision record (engine-provided).</summary>
    public string WorkBranch { get; init; } = "";

    /// <summary>Recorded main base SHA for the revision record (engine-provided).</summary>
    public string MainBaseSha { get; init; } = "";

    /// <summary>Typed role identity of the workspace (never a free-form string).</summary>
    public SandboxRole Role { get; init; } = SandboxRole.Coder;
    public string RunId { get; init; } = "run";
    public string AttemptId { get; init; } = "attempt";

    public MaterializationLimits? Limits { get; init; }

    /// <summary>Trusted crash-journal callback invoked immediately after the exact fresh
    /// attempt root exists. If recording fails, factory cleanup removes the partial attempt.</summary>
    internal Action<string>? AttemptCreated { get; init; }
}

/// <summary>
/// Orchestrates one disposable candidate workspace for a role attempt:
///
///  1. validates the managed root FIRST via <see cref="ValidatedManagedRootPath"/> (canonical
///     absolute NFC POSIX path, not the filesystem root, not HOME, not a generic shared
///     location such as /tmp itself, existing real directory, no symlink/reparse point above
///     or at it) — nothing is created before that validation succeeds;
///  2. creates a fresh random attempt directory (which must not already exist) with empty
///     source/ and ingestion/ subdirectories, entirely inside the protected cleanup scope;
///  3. validates the source path with <see cref="ValidatedSandboxWorkspacePath"/> (strict
///     physical child of the managed root, never a link, never overlapping the authoritative
///     repository in either direction) and retains the validated value;
///  4. materializes the exact candidate commit into the empty source directory via
///     <see cref="GitTreeMaterializer"/> (SHA-selected, binary-safe, limit-enforced);
///  5. verifies the materialized baseline WITHOUT filter-aware `git add`: every materialized
///     file is re-hashed with `hash-object -w --no-filters` and must equal the validated
///     candidate blob OID; the trusted disposable ingestion index is populated with structured
///     `update-index --cacheinfo` entries (validated mode/OID/path) and its tree OID must
///     equal the candidate tree OID exactly;
///  6. builds the agent `.git` independently in its own object store by re-hashing the source
///     bytes (no authoritative or ingestion object is copied or hardlinked), requires its
///     baseline tree AND baseline commit tree to equal the candidate tree, and produces the
///     separate disposable ONE-COMMIT `.git` (no remote, no alternates, no active hooks, no
///     inherited config, no knowledge of authoritative history). It is UNTRUSTED and must
///     never be reused for extraction;
///  7. on ANY failure — including directory-creation failure — removes the complete attempt.
///     If cleanup itself fails, that fact is surfaced explicitly; success is never claimed
///     silently.
/// </summary>
public sealed class CandidateWorkspaceFactory
{
    private readonly IGitService _git;

    public CandidateWorkspaceFactory(IGitService authoritativeGit) => _git = authoritativeGit;

    public CandidateWorkspace Create(CandidateWorkspaceRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!IsFullSha(request.CommitSha))
            throw new InvalidOperationException(
                "the candidate workspace needs a full 40-hex commit SHA resolved by trusted git code.");
        var commitSha = request.CommitSha.ToLowerInvariant();

        // BLOCKER 2: the managed root is fully validated before anything is created.
        var managedRoot = ValidatedManagedRootPath.Create(request.ManagedRoot);

        var attemptRoot = Path.Combine(managedRoot.Value, "attempt-" + Guid.NewGuid().ToString("N"));
        var sourcePath = Path.Combine(attemptRoot, "source");
        var ingestionPath = Path.Combine(attemptRoot, "ingestion");
        try
        {
            // The freshly selected attempt must not already exist, and the source must start
            // empty. Both creations live inside the protected cleanup scope.
            if (Directory.Exists(attemptRoot))
                throw new InvalidOperationException(
                    "the freshly selected attempt directory already exists; refusing to reuse it.");
            Directory.CreateDirectory(sourcePath);
            Directory.CreateDirectory(ingestionPath);
            request.AttemptCreated?.Invoke(attemptRoot);
            if (Directory.EnumerateFileSystemEntries(sourcePath).Any())
                throw new InvalidOperationException(
                    "the freshly created source directory is not empty; refusing to materialize into it.");

            // The mounted directory is the source tree; it must be a validated strict physical
            // child of the managed root that cannot overlap the authoritative repository.
            var validatedSource = ValidatedSandboxWorkspacePath.Create(
                sourcePath, managedRoot.Value, _git.RepoPath);

            var materialized = new GitTreeMaterializer(_git, request.Limits)
                .Materialize(validatedSource.Value, commitSha, ct);

            VerifyAndPopulateIngestion(ingestionPath, validatedSource.Value, materialized);
            BuildAgentRepository(validatedSource.Value, materialized, commitSha);

            return new CandidateWorkspace(
                new CandidateRevision(request.WorkBranch, commitSha, request.MainBaseSha),
                attemptRoot,
                validatedSource.Value,
                ingestionPath,
                materialized.TreeOid,
                request.Role,
                request.RunId,
                request.AttemptId);
        }
        catch (Exception failure)
        {
            try
            {
                if (Directory.Exists(attemptRoot)) Directory.Delete(attemptRoot, recursive: true);
            }
            catch (Exception cleanupFailure)
            {
                // Never silently claim successful cleanup: the original failure stays attached.
                throw new InvalidOperationException(
                    "the failed attempt could not be cleaned up; partial attempt state remains " +
                    "and must be removed by the janitor (" +
                    cleanupFailure.GetType().Name + ").",
                    failure);
            }
            throw;
        }
    }

    /// <summary>
    /// Filter-free verification: every materialized file is re-hashed with
    /// <c>hash-object -w --no-filters</c> into the ingestion repository's own object store and
    /// must equal the validated candidate blob OID (so clean/smudge/eol filters and newline
    /// conversion can never silently rewrite bytes). The trusted ingestion index is populated
    /// with structured cache-info entries and its tree OID must equal the candidate tree OID.
    /// </summary>
    private static void VerifyAndPopulateIngestion(
        string ingestionPath, string sourcePath, MaterializedTree materialized)
    {
        var ingestionGit = GitService.CreateDisposable(ingestionPath);
        ingestionGit.Init();
        foreach (var entry in materialized.Entries)
        {
            var hashed = ingestionGit.HashObjectNoFilters(Path.Combine(sourcePath, entry.Path));
            if (!string.Equals(hashed, entry.ObjectSha, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "materialization verification failed: the bytes on disk do not hash to " +
                    "the candidate blob id; filters, newline conversion or corruption are not " +
                    "tolerated and the workspace is discarded.");
            ingestionGit.UpdateIndexCacheInfo(entry.Mode, hashed, entry.Path);
        }
        var verifiedTree = ingestionGit.WriteTree();
        if (!string.Equals(verifiedTree, materialized.TreeOid, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "materialization verification failed: the trusted ingestion index tree does " +
                "not match the candidate tree; the workspace is discarded.");
    }

    /// <summary>
    /// The agent-visible `.git` is built INDEPENDENTLY in its own object store: source bytes
    /// are re-hashed (no authoritative or ingestion object is copied or hardlinked), the
    /// baseline tree and the baseline commit's tree must both equal the candidate tree, and
    /// the disposable execution profile guarantees no remote, no alternates, no active hooks
    /// and no inherited config. It is untrusted and never used for extraction.
    /// </summary>
    private static void BuildAgentRepository(
        string sourcePath, MaterializedTree materialized, string commitSha)
    {
        var agentGit = GitService.CreateDisposable(sourcePath);
        agentGit.Init();
        // Defense in depth: the empty template guarantees no hooks exist; the local config
        // additionally pins hooks off for the agent's own git usage.
        agentGit.SetLocalConfig("core.hooksPath", "/dev/null");
        foreach (var entry in materialized.Entries)
        {
            var hashed = agentGit.HashObjectNoFilters(Path.Combine(sourcePath, entry.Path));
            if (!string.Equals(hashed, entry.ObjectSha, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "the agent repository baseline does not hash to the candidate blob id; " +
                    "the workspace is discarded.");
            agentGit.UpdateIndexCacheInfo(entry.Mode, hashed, entry.Path);
        }
        var agentTree = agentGit.WriteTree();
        if (!string.Equals(agentTree, materialized.TreeOid, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the agent repository baseline tree does not match the candidate tree; the " +
                "workspace is discarded.");
        var shortSha = commitSha[..Math.Min(12, commitSha.Length)];
        var baselineCommit = agentGit.CommitStaged($"tenninety: baseline candidate {shortSha}")
            ?? agentGit.CommitAllowEmpty($"tenninety: baseline candidate {shortSha} (empty tree)");
        if (!string.Equals(
                agentGit.ResolveTreeOfCommit(baselineCommit),
                materialized.TreeOid,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the agent baseline commit's tree does not match the candidate tree; the " +
                "workspace is discarded.");
    }

    private static bool IsFullSha(string? value) =>
        value is { Length: 40 } &&
        value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));
}
