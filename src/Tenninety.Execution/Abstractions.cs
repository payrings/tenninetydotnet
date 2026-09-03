using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Testing;

namespace Tenninety.Execution;

/// <summary>
/// Coder-only logical context. It contains the exact trusted candidate identity and bounded
/// instructions, but cannot carry an authoritative repository path, a host scratch path,
/// Docker arguments, mounts, or a process launcher.
/// </summary>
public sealed class CoderRunContext
{
    public required CandidateRevision Candidate { get; init; }
    public required WorkPackage WorkPackage { get; init; }
    public GlobalContext? Global { get; init; }
    public int Attempt { get; init; }
    public IReadOnlyList<string> Feedback { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Advice { get; init; } = Array.Empty<string>();

    public void Validate() => RoleRunContextValidation.Validate(
        Candidate, WorkPackage, Attempt, "coder");
}

/// <summary>
/// Reviewer-only logical context. The reviewer receives an exact committed candidate and the
/// review instructions; repository exploration happens only inside its fresh offline guest.
/// </summary>
public sealed class ReviewerRunContext
{
    public required CandidateRevision Candidate { get; init; }
    public required WorkPackage WorkPackage { get; init; }
    public GlobalContext? Global { get; init; }
    public int Attempt { get; init; }
    public IReadOnlyList<string> Feedback { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Advice { get; init; } = Array.Empty<string>();

    public void Validate() => RoleRunContextValidation.Validate(
        Candidate, WorkPackage, Attempt, "reviewer");
}

internal static class RoleRunContextValidation
{
    public static void Validate(
        CandidateRevision candidate, WorkPackage workPackage, int attempt, string role)
    {
        if (candidate is null || !TesterRunContext.IsFullCommitSha(candidate.CommitSha) ||
            !TesterRunContext.IsFullCommitSha(candidate.MainBaseSha) ||
            string.IsNullOrWhiteSpace(candidate.WorkBranch))
            throw new InvalidOperationException(
                $"the {role} context requires an exact trusted candidate revision.");
        if (workPackage is null ||
            !TesterRunContext.IsValidWorkPackageIdentifier(workPackage.Id))
            throw new InvalidOperationException(
                $"the {role} context requires a validated work-package identity.");
        if (attempt < 1)
            throw new InvalidOperationException(
                $"the {role} attempt number must be at least one.");
    }
}

public interface ICoderAgent
{
    Task<CoderResult> ImplementAsync(CoderRunContext ctx, CancellationToken ct = default);
}

public interface IReviewerAgent
{
    Task<ReviewResult> ReviewAsync(ReviewerRunContext ctx, CancellationToken ct = default);
}

/// <summary>
/// Mechanical test gate. Receives a Tester-only <see cref="TesterRunContext"/> (never the
/// coder/reviewer contexts): the trusted candidate identity, the validated
/// work-package identifier, the attempt number and a defensive advice snapshot. No host
/// path, mount, Docker argument or process launcher can travel through this interface.
/// </summary>
public interface ITesterAgent
{
    Task<TestRunResult> RunTestsAsync(TesterRunContext ctx, CancellationToken ct = default);
}
