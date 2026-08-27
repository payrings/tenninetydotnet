using System.Text.Json.Serialization;

namespace Tenninety.Core.Models;

public sealed class ReviewResult
{
    public bool Passed { get; init; }
    public List<string> Reasons { get; init; } = new();
    public string ReviewerModel { get; init; } = "";
}

public sealed class TestRunResult
{
    public bool Passed { get; init; }
    public int ExitCode { get; init; }
    public string OutputTail { get; init; } = "";
    public string Command { get; init; } = "";
}

public sealed class CoderResult
{
    public bool ProducedChanges { get; init; }
    public string? CommitSha { get; init; }
    public string Summary { get; init; } = "";
    public List<string> FilesTouched { get; init; } = new();
}

/// <summary>Frontier repair advice returned on attempt-10 escalation (Part IV.3).</summary>
public sealed class RepairAdvice
{
    public string Analysis { get; init; } = "";
    public List<string> Advice { get; init; } = new();
}

/// <summary>Frontier pivot analysis: KEEP / REWORK / CANCEL lists plus optional new WPs (Part IV.4).</summary>
public sealed class PivotProposal
{
    public List<string> Keep { get; init; } = new();
    public List<PivotRework> Rework { get; init; } = new();
    public List<PivotCancel> Cancel { get; init; } = new();
    [JsonPropertyName("new_work_packages")]
    public List<WorkPackage> NewWorkPackages { get; init; } = new();
    public string Rationale { get; init; } = "";
}

public sealed class PivotRework
{
    public string Id { get; init; } = "";
    public string Reason { get; init; } = "";
    [JsonPropertyName("updated_directives")]
    public List<string> UpdatedDirectives { get; init; } = new();
}

public sealed class PivotCancel
{
    public string Id { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>Frontier guidance for reverting a bad promotion (Part IV.5).</summary>
public sealed class RevertGuidance
{
    [JsonRequired]
    public string Analysis { get; init; } = "";
    [JsonRequired]
    public List<string> Steps { get; init; } = new();
    [JsonPropertyName("mechanical_revert_sufficient")]
    [JsonRequired]
    public bool MechanicalRevertSufficient { get; init; }
}
