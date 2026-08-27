using Tenninety.Core.Models;

namespace Tenninety.Execution;

/// <summary>Everything a local executor needs for one attempt at a work package.</summary>
public sealed class WpContext
{
    public required string RepoPath { get; init; }
    public required WorkPackage WorkPackage { get; init; }
    public GlobalContext? Global { get; init; }
    public int Attempt { get; init; }
    public IReadOnlyList<string> Feedback { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Advice { get; init; } = Array.Empty<string>();

    /// <summary>Bounded, sanitised unified diff vs main for this attempt (reviewer input).</summary>
    public string DiffPatch { get; init; } = "";
}

public interface ICoderAgent
{
    Task<CoderResult> ImplementAsync(WpContext ctx, CancellationToken ct = default);
}

public interface IReviewerAgent
{
    Task<ReviewResult> ReviewAsync(WpContext ctx, CancellationToken ct = default);
}

public interface ITesterAgent
{
    Task<TestRunResult> RunTestsAsync(WpContext ctx, CancellationToken ct = default);
}
