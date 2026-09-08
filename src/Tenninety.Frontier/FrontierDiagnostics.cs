using System.Runtime.CompilerServices;
using Tenninety.Core.Security;

[assembly: InternalsVisibleTo("Tenninety.Tests")]

namespace Tenninety.Frontier;

/// <summary>
/// THE single construction point for every model-controlled Frontier diagnostic. Whatever the
/// exception path — an HTTP error body, a non-JSON response, a parser message, a JSON
/// extraction error, or a plan-validation error — remote/model-controlled text must pass
/// through <see cref="Build"/> before it can reach a log, the terminal or an exception
/// message:
///
///  1. SECRET REDACTION first (so bounding can never strip a secret's identifying prefix
///     while keeping its value);
///  2. control-character stripping (C0, DEL, C1) — no terminal escape sequences, no
///     carriage-return line rewriting, no NUL bytes;
///  3. the final length bound (<see cref="FrontierCallException.MaxDiagnosticChars"/>),
///     applied LAST to the fully assembled diagnostic.
///
/// The order is deliberate and fixed; callers never re-implement any step.
/// </summary>
internal static class FrontierDiagnostics
{
    /// <summary>Bounded user-facing suffix appended when a diagnostic is cut.</summary>
    private const string TruncationMarker = "…";

    /// <summary>Applies redaction, control-character stripping and the final length bound to
    /// one model/remote-controlled fragment. Safe for null input; never returns text longer
    /// than <see cref="FrontierCallException.MaxDiagnosticChars"/>.</summary>
    public static string Build(string? raw)
    {
        var sanitized = Sanitizer.SanitizeDiagnostic(raw ?? "");
        return sanitized.Length <= FrontierCallException.MaxDiagnosticChars
            ? sanitized
            : sanitized[..(FrontierCallException.MaxDiagnosticChars - TruncationMarker.Length)] +
              TruncationMarker;
    }

    /// <summary>Assembles a multi-part diagnostic from model/remote-controlled fragments:
    /// every fragment is sanitized and bounded INDIVIDUALLY, the joined text is bounded
    /// again, so the result never exceeds
    /// <see cref="FrontierCallException.MaxDiagnosticChars"/> regardless of how many parts
    /// the model managed to produce.</summary>
    public static string BuildJoined(string separator, IEnumerable<string?> fragments)
    {
        var sanitized = fragments
            .Where(fragment => !string.IsNullOrEmpty(fragment))
            .Select(fragment => Sanitizer.SanitizeDiagnostic(fragment!));
        return Build(string.Join(separator, sanitized));
    }
}
