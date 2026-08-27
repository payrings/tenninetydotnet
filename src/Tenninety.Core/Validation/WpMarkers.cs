using System.Text.RegularExpressions;
using Tenninety.Core.Models;

namespace Tenninety.Core.Validation;

/// <summary>
/// Blueprint v3.2 Enterprise ambiguity protocol markers, carried in <see cref="WorkPackage.Notes"/>.
/// <list type="bullet">
/// <item><c>AMBIGUOUS</c> — a critical detail was missing; the WP still carries directives built on
/// recorded assumptions and remains executable, but the human should review it.</item>
/// <item><c>CONFLICT</c> — contradictory business rules; per protocol the Architect generates NO
/// directives. The orchestrator never schedules such WPs; human resolution (typically via a pivot
/// REWORK) is required.</item>
/// </list>
/// Detection is case-SENSITIVE uppercase-token matching, exactly as the blueprint protocol writes
/// them, so ordinary prose ("we resolved the conflict…") can never re-trigger the protocol.
/// </summary>
public static partial class WpMarkers
{
    [GeneratedRegex(@"\b(AMBIGUOUS|CONFLICT)\b", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    public const string Conflict = "CONFLICT";
    public const string Ambiguous = "AMBIGUOUS";

    public static bool IsConflict(WorkPackage wp) => HasMarker(wp, Conflict);

    public static bool IsAmbiguous(WorkPackage wp) => HasMarker(wp, Ambiguous);

    /// <summary>All distinct markers present on the package (empty when none).</summary>
    public static List<string> MarkersOf(WorkPackage wp)
    {
        var found = new List<string>();
        foreach (var marker in new[] { Ambiguous, Conflict })
            if (HasMarker(wp, marker))
                found.Add(marker);
        return found;
    }

    private static bool HasMarker(WorkPackage wp, string marker) =>
        !string.IsNullOrWhiteSpace(wp.Notes) &&
        MarkerPattern().Matches(wp.Notes).Any(m => m.Groups[1].Value == marker);
}
