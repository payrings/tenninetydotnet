using System.Text.Json.Serialization;

namespace Tenninety.Core.Models;

public sealed class ReviewResult
{
    public bool Passed { get; init; }
    public List<string> Reasons { get; init; } = new();
    public string ReviewerModel { get; init; } = "";

    /// <summary>Exact committed candidate inspected by the reviewer. A passing result with a
    /// missing or mismatched identity is rejected by the engine.</summary>
    public string? CandidateSha { get; init; }

}

public sealed class TestRunResult
{
    public bool Passed { get; init; }
    public int ExitCode { get; init; }
    public string OutputTail { get; init; } = "";
    public string Command { get; init; } = "";

    /// <summary>Exact candidate commit SHA the tester ran against, supplied by trusted
    /// orchestration. A missing or mismatched value is never accepted as a passing gate by
    /// the engine or the hotfix flow; it is never "repaired" from the current HEAD.</summary>
    public string? CandidateSha { get; init; }

    /// <summary>Deterministic digest of accepted Restore-derived output, when the optional
    /// restricted Restore phase ran.</summary>
    public string? RestoreOutputSha256 { get; init; }
}

public enum CoderOutcome
{
    Unspecified,
    ChangesProduced,
    NoChanges,
    CommandFailed,
    PolicyRejected,
}

public sealed class CoderResult
{
    public required CoderOutcome Outcome { get; init; }
    public bool ProducedChanges => Outcome == CoderOutcome.ChangesProduced;
    public string? CommitSha { get; init; }
    public string Summary { get; init; } = "";
    public List<string> FilesTouched { get; init; } = new();
    public List<string> FailureReasons { get; init; } = new();
}

/// <summary>Frontier repair advice returned on attempt-10 escalation (Part IV.3).</summary>
public sealed class RepairAdvice
{
    public const int MaxAnalysisChars = 4000;
    public const int MaxAdviceItems = 20;
    public const int MaxAdviceItemChars = 2000;
    public const int MaxCombinedContentChars = 20_000;

    [JsonRequired]
    public string Analysis { get; init; } = "";
    [JsonRequired]
    public List<string> Advice { get; init; } = new();

    /// <summary>Validates the untrusted repair contract and returns detached content so the
    /// caller can prepare a complete state update before publishing any mutation.</summary>
    public static RepairAdvice ValidateAndCopy(RepairAdvice? response)
    {
        if (response is null)
            throw new InvalidOperationException("frontier repair advice was null.");
        if (string.IsNullOrWhiteSpace(response.Analysis))
            throw new InvalidOperationException("frontier repair analysis is missing or empty.");
        if (response.Analysis.Length > MaxAnalysisChars)
            throw new InvalidOperationException("frontier repair analysis exceeds its size bound.");
        if (response.Advice is null)
            throw new InvalidOperationException("frontier repair advice collection is null.");
        if (response.Advice.Count is 0 or > MaxAdviceItems)
            throw new InvalidOperationException(
                $"frontier repair advice must contain 1 to {MaxAdviceItems} actions.");

        var combinedChars = response.Analysis.Length;
        var accepted = new List<string>(response.Advice.Count);
        foreach (var item in response.Advice)
        {
            if (string.IsNullOrWhiteSpace(item))
                throw new InvalidOperationException(
                    "frontier repair advice contains a null or empty action.");
            if (item.Length > MaxAdviceItemChars)
                throw new InvalidOperationException(
                    "frontier repair advice action exceeds its size bound.");
            combinedChars += item.Length;
            if (combinedChars > MaxCombinedContentChars)
                throw new InvalidOperationException(
                    "frontier repair advice exceeds its combined content bound.");
            accepted.Add(item);
        }

        return new RepairAdvice
        {
            Analysis = response.Analysis,
            Advice = accepted,
        };
    }
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
