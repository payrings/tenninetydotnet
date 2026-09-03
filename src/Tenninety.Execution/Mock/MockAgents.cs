using System.Text;
using Tenninety.Core;
using Tenninety.Core.Models;

namespace Tenninety.Execution.Mock;

/// <summary>
/// Offline coder: materializes the WP as a deterministic implementation note file and commits it.
/// Lets the full queue run end-to-end with no models (Phase 1 exit criterion).
/// </summary>
public sealed class MockCoderAgent : ICoderAgent
{
    private readonly string? _authoritativeRepositoryPath;

    public MockCoderAgent(string? authoritativeRepositoryPath = null) =>
        _authoritativeRepositoryPath = authoritativeRepositoryPath;

    public Task<CoderResult> ImplementAsync(CoderRunContext ctx, CancellationToken ct = default)
    {
        ctx.Validate();
        var repoPath = _authoritativeRepositoryPath
            ?? throw new InvalidOperationException(
                "the mock coder needs the trusted repository binding supplied by orchestration.");
        var dir = Path.Combine(repoPath, "app");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{ctx.WorkPackage.Id}.implementation.md");

        var sb = new StringBuilder();
        sb.AppendLine($"# {ctx.WorkPackage.Id} — {ctx.WorkPackage.Title}");
        sb.AppendLine($"Layer: {ctx.WorkPackage.Layer}");
        sb.AppendLine($"Goal: {ctx.WorkPackage.Goal}");
        sb.AppendLine();
        sb.AppendLine("## Directives");
        foreach (var d in ctx.WorkPackage.Directives) sb.AppendLine($"- {d}");
        sb.AppendLine();
        sb.AppendLine("## Acceptance Criteria");
        foreach (var a in ctx.WorkPackage.AcceptanceCriteria) sb.AppendLine($"- {a}");
        if (!string.IsNullOrWhiteSpace(ctx.WorkPackage.Notes))
        {
            sb.AppendLine();
            sb.AppendLine($"## Notes ({ctx.WorkPackage.Module})");
            sb.AppendLine(ctx.WorkPackage.Notes.Trim());
        }
        if (ctx.Advice.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Frontier Repair Advice Applied");
            foreach (var a in ctx.Advice) sb.AppendLine($"- {a}");
        }
        if (ctx.Feedback.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Feedback Incorporated");
            foreach (var f in ctx.Feedback.TakeLast(5)) sb.AppendLine($"- {f}");
        }
        File.WriteAllText(file, sb.ToString());

        var summary = $"attempt {ctx.Attempt}: materialized directives for {ctx.WorkPackage.Id}";
        return Task.FromResult(new CoderResult
        {
            ProducedChanges = true,
            Summary = summary,
            FilesTouched = new List<string> { Path.GetRelativePath(repoPath, file) },
        });
    }
}

/// <summary>Deterministic reviewer: fails the first N attempts of a phase, always passes once frontier advice exists.</summary>
public sealed class MockReviewerAgent : IReviewerAgent
{
    private readonly int _failAttempts;
    private readonly bool _ignoresAdvice;

    public MockReviewerAgent(int failAttempts = 0, bool ignoresAdvice = false)
    {
        _failAttempts = failAttempts;
        _ignoresAdvice = ignoresAdvice;
    }

    public Task<ReviewResult> ReviewAsync(ReviewerRunContext ctx, CancellationToken ct = default)
    {
        ctx.Validate();
        if (!_ignoresAdvice && (ctx.Advice.Count > 0 || ctx.Attempt > _failAttempts))
            return Task.FromResult(new ReviewResult
            {
                Passed = true,
                ReviewerModel = "mock-reviewer",
                CandidateSha = ctx.Candidate.CommitSha,
            });

        return Task.FromResult(new ReviewResult
        {
            Passed = false,
            ReviewerModel = "mock-reviewer",
            CandidateSha = ctx.Candidate.CommitSha,
            Reasons = new List<string>
            {
                $"Directive not yet demonstrably satisfied: '{ctx.WorkPackage.Directives[Math.Min(ctx.Attempt - 1, ctx.WorkPackage.Directives.Count - 1)]}'.",
                "Implementation note does not map every directive to a concrete change.",
            },
        });
    }
}
