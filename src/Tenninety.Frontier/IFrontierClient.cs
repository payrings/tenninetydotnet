using Tenninety.Core.Models;

namespace Tenninety.Frontier;

public sealed record RepairRequest(
    WorkPackage WorkPackage, int TotalAttempts, IReadOnlyList<string> Feedback,
    string? PreviousAdvice, string RecentAuditTail, string SanitizedDiff);

public sealed record PivotRequest(string SpecSnapshot, string PlanJson, string UserIntent, string AuditTail);

public sealed record RevertRequest(string CommitInfo, string SanitizedDiff, string Reason);

/// <summary>The Frontier Model surface consumed by the orchestrator (Part I triad). Only the host calls it.</summary>
public interface IFrontierClient
{
    Task<Plan> GeneratePlanAsync(string sanitizedSpecMarkdown, CancellationToken ct = default);
    Task<RepairAdvice> GetRepairAdviceAsync(RepairRequest request, CancellationToken ct = default);
    Task<PivotProposal> ProposePivotAsync(PivotRequest request, CancellationToken ct = default);
    Task<RevertGuidance> ProposeRevertAsync(RevertRequest request, CancellationToken ct = default);
}
