using Tenninety.Execution.Sandbox;

namespace Tenninety.Execution.Candidates;

/// <summary>The durable candidate: a host Git commit SHA on the work branch. Container
/// filesystems are never the durable candidate.</summary>
public sealed record CandidateRevision(
    string WorkBranch,
    string CommitSha,
    string MainBaseSha);

/// <summary>
/// One disposable, role-scoped workspace materialized from an exact candidate commit. All paths
/// are host scratch paths owned by the trusted control plane; agents only ever see the
/// container-side /workspace view of <see cref="SourcePath"/>.
///
/// Trust boundary: construction is INTERNAL and every property is get-only, so an existing
/// workspace can never be relabeled (a reviewer/tester workspace stays reviewer/tester) or
//  have its identity fields rewritten after creation. The role is the typed
/// <see cref="SandboxRole"/>, not a free-form string.
/// </summary>
public sealed class CandidateWorkspace
{
    internal CandidateWorkspace(
        CandidateRevision revision,
        string attemptRoot,
        string sourcePath,
        string trustedIngestionPath,
        string baselineTreeOid,
        SandboxRole role,
        string runId,
        string attemptId)
    {
        Revision = revision;
        AttemptRoot = attemptRoot;
        SourcePath = sourcePath;
        TrustedIngestionPath = trustedIngestionPath;
        BaselineTreeOid = baselineTreeOid;
        Role = role;
        RunId = runId;
        AttemptId = attemptId;
    }

    public CandidateRevision Revision { get; }
    /// <summary>Private attempt root containing the source tree, ingestion tree and metadata.</summary>
    public string AttemptRoot { get; }
    /// <summary>Disposable materialized source tree (the role's /workspace content on the host).</summary>
    public string SourcePath { get; }
    /// <summary>Trusted ingestion repository used to derive and verify the promotion patch.</summary>
    public string TrustedIngestionPath { get; }
    /// <summary>Git tree OID of the exact candidate commit the source was materialized from.</summary>
    public string BaselineTreeOid { get; }
    public SandboxRole Role { get; }
    /// <summary>Non-secret run identity.</summary>
    public string RunId { get; }
    /// <summary>Non-secret attempt identity.</summary>
    public string AttemptId { get; }
}

/// <summary>Git change kinds accepted by the v1 promotion pipeline. Renames are represented as
/// delete plus add; rename detection is presentation only and never security relevant.</summary>
public enum GitChangeKind
{
    Added,
    Modified,
    Deleted,
    TypeChanged,
}

/// <summary>One validated change between the candidate baseline and the trusted ingestion tree.</summary>
public sealed record CandidateChange(
    /// <summary>Normalized repository-relative path (forward slashes, no traversal).</summary>
    string NormalizedPath,
    GitChangeKind Kind,
    /// <summary>Git file mode, e.g. "100644"/"100755"; empty for additions.</summary>
    string OldMode,
    /// <summary>Git file mode; empty for deletions.</summary>
    string NewMode,
    /// <summary>Object hashes when available (never for agent-supplied data).</summary>
    string? OldObjectHash,
    string? NewObjectHash,
    long ByteSize);
