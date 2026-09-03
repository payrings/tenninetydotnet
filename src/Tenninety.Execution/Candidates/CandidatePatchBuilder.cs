using System.Security.Cryptography;
using Tenninety.Execution.Sandbox;
using Tenninety.Git;

namespace Tenninety.Execution.Candidates;

/// <summary>
/// Inert diagnostic metadata about a validated candidate patch. This snapshot CANNOT be passed
/// to any apply API — the actionable validated patch is the internal opaque
/// <see cref="ValidatedCandidatePatch"/> capability created only by the builder after the
/// policy accepted the candidate.
/// </summary>
public sealed record CandidatePatch(
    string BaseCommitSha,
    string BaseTreeOid,
    string TargetTreeOid,
    string PatchFilePath,
    string PatchSha256,
    IReadOnlyList<CandidateChange> Changes);

/// <summary>
/// The actionable validated patch: an OPAQUE, non-forgeable capability. Construction is
/// internal and happens ONLY in <see cref="CandidatePatchBuilder.Build"/> after the scan and
/// policy succeeded. It binds the patch to the exact workspace identity (attempt root, run,
/// attempt, role), the base commit/tree, the trusted target tree, the frozen validated
/// manifest and the SHA-256 of the exact patch bytes that will be applied.
/// </summary>
internal sealed class ValidatedCandidatePatch
{
    internal ValidatedCandidatePatch(
        CandidateWorkspace workspace,
        string baseTreeOid,
        string targetTreeOid,
        byte[] patchBytes,
        string auditFilePath,
        IReadOnlyList<CandidateChange> changes)
    {
        WorkspaceAttemptRoot = workspace.AttemptRoot;
        WorkspaceRunId = workspace.RunId;
        WorkspaceAttemptId = workspace.AttemptId;
        WorkspaceRole = workspace.Role;
        BaseCommitSha = workspace.Revision.CommitSha;
        WorkBranch = workspace.Revision.WorkBranch;
        MainBaseSha = workspace.Revision.MainBaseSha;
        BaseTreeOid = baseTreeOid;
        TargetTreeOid = targetTreeOid;
        // Defensive freeze: the stored bytes can never be mutated after the SHA-256 was
        // recorded, and the change manifest is a frozen snapshot.
        _frozenPatchBytes = (byte[])patchBytes.Clone();
        PatchByteLength = _frozenPatchBytes.Length;
        AuditFilePath = auditFilePath;
        PatchSha256 = Convert.ToHexString(SHA256.HashData(_frozenPatchBytes)).ToLowerInvariant();
        Changes = changes.ToArray();
    }

    public string WorkspaceAttemptRoot { get; }
    public string WorkspaceRunId { get; }
    public string WorkspaceAttemptId { get; }
    public Sandbox.SandboxRole WorkspaceRole { get; }
    public string BaseCommitSha { get; }
    public string WorkBranch { get; }
    public string MainBaseSha { get; }
    public string BaseTreeOid { get; }
    public string TargetTreeOid { get; }
    /// <summary>Exact byte count of the frozen patch bytes.</summary>
    public int PatchByteLength { get; }
    public string AuditFilePath { get; }
    public string PatchSha256 { get; }
    public IReadOnlyList<CandidateChange> Changes { get; }

    private readonly byte[] _frozenPatchBytes;

    /// <summary>Returns a DEFENSIVE COPY of the exact frozen patch bytes for piping to Git.
    /// The stored array is never exposed, so it cannot be mutated after the SHA-256 was
    /// recorded.</summary>
    internal byte[] GetFrozenPatchBytesSnapshot() => (byte[])_frozenPatchBytes.Clone();

    /// <summary>Inert diagnostic snapshot safe to expose publicly.</summary>
    public CandidatePatch ToInertSnapshot() => new(
        BaseCommitSha, BaseTreeOid, TargetTreeOid, AuditFilePath, PatchSha256, Changes);
}

/// <summary>
/// Builds the host-authored promotion patch from the opaque validated scan result. The patch
/// is generated as `git diff --binary --full-index --no-ext-diff --no-renames --no-color`
/// (replacements disabled) between the verified baseline and target trees in the trusted
/// ingestion repository — renames are delete plus add; binary files, deletions and
/// executable-mode changes are fully supported. The bytes are size-capped, SHA-256 hashed,
/// and persisted ONCE with `FileMode.CreateNew` beneath the canonical attempt root (existing
/// paths, links and escapes rejected; owner-only permissions on Linux). The Coder never
/// supplies any of this data.
/// </summary>
public sealed class CandidatePatchBuilder
{
    internal ValidatedCandidatePatch Build(
        CandidateWorkspace workspace,
        CandidateScanResult scan,
        long maxPatchBytes,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(scan);
        if (maxPatchBytes is < 1 or > 1_073_741_824)
            throw new InvalidOperationException(
                $"the patch size limit must be within [1, 1073741824] but is {maxPatchBytes}.");
        ct.ThrowIfCancellationRequested();

        var ingestionGit = GitService.CreateDisposable(workspace.TrustedIngestionPath);
        byte[] patchBytes;
        try
        {
            patchBytes = ingestionGit.DiffTreesRaw(
                workspace.BaselineTreeOid, scan.TargetTreeOid, maxPatchBytes);
        }
        catch (GitOutputLimitExceededException ex)
        {
            throw new InvalidOperationException(
                "the candidate patch exceeds the configured maximum patch size; promotion " +
                "fails closed.", ex);
        }
        if (patchBytes.Length > maxPatchBytes)
            throw new InvalidOperationException(
                "the candidate patch exceeds the configured maximum patch size; promotion " +
                "fails closed.");

        // Persist the audit copy exactly once: CreateNew under the canonical attempt root,
        // rejecting existing paths, links and escapes; owner-only on Linux.
        var auditPath = Path.GetFullPath(Path.Combine(workspace.AttemptRoot, "promotion.patch"));
        if (!auditPath.StartsWith(
                Path.GetFullPath(workspace.AttemptRoot) + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the audit copy path escaped the canonical attempt root; promotion fails closed.");
        if (File.Exists(auditPath))
            throw new InvalidOperationException(
                "the audit copy path already exists; promotion fails closed.");
        if (TrustedPathValidation.IsReparsePoint(
                Path.GetDirectoryName(auditPath)!))
            throw new InvalidOperationException(
                "the audit copy directory is a symlink/reparse point; promotion fails closed.");
        using (var audit = new FileStream(
                   auditPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            audit.Write(patchBytes, 0, patchBytes.Length);
            audit.Flush(true);
        }
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(auditPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new ValidatedCandidatePatch(workspace, workspace.BaselineTreeOid,
            scan.TargetTreeOid, patchBytes, auditPath, scan.Changes);
    }
}
