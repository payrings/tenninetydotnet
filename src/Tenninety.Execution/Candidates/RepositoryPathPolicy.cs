using System.Text;


namespace Tenninety.Execution.Candidates;

/// <summary>
/// Strict policy for repository-relative tree paths that may be materialized into a disposable
/// sandbox workspace. v1 fails closed on anything that could escape the destination, confuse a
/// delimiter, collide, or change under normalization:
///  - empty paths and paths with empty/'.'/'..' segments (absolute, traversal, 'a//b', trailing
///    slash and './x' forms are all segment violations),
///  - NUL, backslashes AND every colon character: rooted syntax is rejected independently of
///    the host operating system — POSIX absolute, Windows drive-absolute (C:/x), drive-relative
///    (C:x), UNC/device forms and alternate-data-stream ambiguity are all unreachable in v1,
///  - control characters including TAB/LF/CR delimiters (unusual delimiters that corrupt
///    manifests and diffs — rejection is the safe v1 handling),
///  - any path that is not already in canonical NFC Unicode form (a decomposed form would
///    change under normalization),
///  - EVERY path segment equal to ".git", compared ordinally and case-insensitively (".GIT",
///    "dir/.git/config", …), because the disposable agent repository lives at that reserved
///    name. Tracked framework state such as '.tenninety/…' is ordinary candidate content.
/// </summary>
public static class RepositoryPathPolicy
{
    public const string RegularFileMode = "100644";
    public const string ExecutableFileMode = "100755";

    /// <summary>v1 materializes regular blobs only: 100644 and 100755. Symlinks (120000),
    /// gitlinks/submodules (160000), trees in invalid positions and any unknown mode fail
    /// closed.</summary>
    public static bool IsSupportedMode(string? mode) =>
        mode is RegularFileMode or ExecutableFileMode;

    /// <summary>The canonical normalized representation of a candidate path: v1 accepts only
    /// printable ASCII, so the path itself IS its canonical representation (ASCII is
    /// NFC-stable by construction). Collision checks therefore compare the path directly,
    /// case-insensitively for case-collisions and ordinally for exact duplicates.</summary>
    public static string Canonicalize(string path) => path;

    public static bool IsSafeTreePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.Contains('\0') || path.Contains('\\') || path.Contains(':')) return false;
        if (path.Any(char.IsControl)) return false;
        if (path.StartsWith('/')) return false;
        // NFC policy: a path must never change under canonical NFC normalization. The host
        // runtime may run with invariant globalization (this repository does), where the
        // normalization APIs are silent no-ops and cannot detect non-NFC forms — so v1
        // accepts only printable ASCII paths, which are NFC-stable by construction. Every
        // non-ASCII path fails closed.
        if (!path.All(c => c >= 0x20 && c <= 0x7E)) return false;
        var segments = path.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or "..")) return false;
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }
}
