using Tenninety.Execution.Candidates;

namespace Tenninety.Execution.Testing;

/// <summary>
/// Tester-only logical context for one mechanical test attempt. It carries exactly the data the
/// Tester needs and nothing else:
///
///  - the trusted <see cref="CandidateRevision"/> (work branch, exact candidate commit SHA,
///    recorded main base) supplied by trusted orchestration — never derived from test output
///    or from the disposable candidate `.git`;
///  - the validated work-package identifier (bounded ASCII identifier for labels, structured
///    environment values and the {wp} template compatibility path);
///  - the attempt number;
///  - a defensive snapshot of frontier advice (drives the existing mock failure-window escape).
///
/// It deliberately cannot carry: the authoritative repository path, a host workspace path, the
/// ingestion path, arbitrary mounts, Docker arguments, or a host process launcher. The Coder
/// and Reviewer keep their existing <see cref="Tenninety.Execution.WpContext"/>.
/// </summary>
public sealed class TesterRunContext
{
    /// <summary>Hard bound for a work-package identifier (matches plan validation ids plus HOTFIX).</summary>
    public const int MaxWorkPackageIdLength = 64;

    private IReadOnlyList<string>? _advice;

    /// <summary>Trusted candidate identity chosen by orchestration (engine reviewedTip or the
    /// recorded post-revert hotfix SHA).</summary>
    public required CandidateRevision Candidate { get; init; }

    /// <summary>Validated bounded ASCII work-package identifier.</summary>
    public required string WorkPackageId { get; init; }

    /// <summary>1-based attempt number within the phase.</summary>
    public required int Attempt { get; init; }

    /// <summary>Defensive advice snapshot: later mutation of the source list can never change
    /// what this context carries. Empty and blank entries are dropped.</summary>
    public IReadOnlyList<string> Advice
    {
        get => _advice ?? Array.Empty<string>();
        init => _advice = value is null
            ? Array.Empty<string>()
            : value.Where(a => !string.IsNullOrWhiteSpace(a)).Take(32).ToList().AsReadOnly();
    }

    /// <summary>Fails closed on any value trusted orchestration must never produce: a
    /// malformed candidate SHA, a hostile work-package identifier or a non-positive attempt.</summary>
    public void Validate()
    {
        if (Candidate is null)
            throw new InvalidOperationException(
                "the tester context carries no candidate identity; refusing to run.");
        if (!IsFullCommitSha(Candidate.CommitSha))
            throw new InvalidOperationException(
                "the tester candidate must be a full 40-character lowercase hex commit SHA " +
                $"resolved by trusted git code, got '{Bounded(Candidate.CommitSha)}'.");
        if (string.IsNullOrWhiteSpace(Candidate.WorkBranch))
            throw new InvalidOperationException(
                "the tester candidate carries no work-branch identity.");
        if (!IsValidWorkPackageIdentifier(WorkPackageId))
            throw new InvalidOperationException(
                "the tester work-package identifier must be a bounded ASCII identifier " +
                "(letters, digits, hyphens, underscores; 1-64 characters), got " +
                $"'{Bounded(WorkPackageId)}'.");
        if (Attempt < 1)
            throw new InvalidOperationException(
                $"the tester attempt number must be >= 1 but is {Attempt}.");
    }

    /// <summary>Full 40-character lowercase hex commit SHA (git always reports lowercase).</summary>
    public static bool IsFullCommitSha(string? value) =>
        value is { Length: 40 } &&
        value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    /// <summary>Bounded ASCII identifier: letters, digits, hyphens, underscores only. This is
    /// the gate for every {wp} template substitution and structured identity value.</summary>
    public static bool IsValidWorkPackageIdentifier(string? value) =>
        value is { Length: >= 1 and <= MaxWorkPackageIdLength } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Bounded(string? value) =>
        string.IsNullOrEmpty(value) ? "<null>"
            : value.Length <= 32 ? value
            : value[..32] + "…";
}
