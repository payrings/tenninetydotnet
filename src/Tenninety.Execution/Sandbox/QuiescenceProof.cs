namespace Tenninety.Execution.Sandbox;

/// <summary>
/// A CONFIRMED quiescence capability issued only by trusted sandbox-runtime code. It is bound
/// to the exact run, attempt, role and workspace it authorizes, so a proof for another
/// attempt cannot be replayed and ordinary callers cannot manufacture confirmation.
///
/// Trust boundary: the constructor and the issuing factory are INTERNAL — the runtime
/// (Phase 4) and the test harness (via InternalsVisibleTo) can issue proofs, but no public
/// constructor, factory or `with`-mutation exists. A failed stop/inspect operation must NOT
/// issue a confirmed capability: the runtime surfaces the failure instead, the workspace is
/// retained/quarantined, and nothing is scanned.
/// </summary>
public sealed class QuiescenceProof
{
    internal QuiescenceProof(
        string runId, string attemptId, SandboxRole role,
        string workspaceAttemptRoot, string evidence)
    {
        RunId = runId;
        AttemptId = attemptId;
        Role = role;
        WorkspaceAttemptRoot = workspaceAttemptRoot;
        Evidence = evidence;
    }

    public string RunId { get; }
    public string AttemptId { get; }
    public SandboxRole Role { get; }
    /// <summary>The canonical workspace identity (validated attempt root) this proof authorizes.</summary>
    public string WorkspaceAttemptRoot { get; }
    public string Evidence { get; }

    /// <summary>Trusted issuance point for the sandbox runtime (and the test harness).</summary>
    internal static QuiescenceProof Issue(
        string runId, string attemptId, SandboxRole role,
        string workspaceAttemptRoot, string evidence) =>
        new(runId, attemptId, role, workspaceAttemptRoot, evidence);
}
