using System.Text;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Shared trusted path-policy helpers used by every sandbox path value object. All checks are
/// deterministic POSIX string logic (never host <see cref="Path"/> semantics), reject rooted
/// syntax independently of the host operating system (POSIX absolute, Windows drive-absolute,
/// drive-relative, UNC/device and alternate-data-stream forms are unreachable because NUL,
/// backslash AND colon are all rejected in v1), and accept PRINTABLE ASCII ONLY.
///
/// The ASCII-only policy exists because this repository builds with
/// <c>InvariantGlobalization=true</c>, where the Unicode normalization APIs
/// (<see cref="string.IsNormalized"/>/<see cref="string.Normalize"/>) are silent no-ops that
/// cannot detect non-NFC forms. Printable ASCII is NFC-stable by construction, so restricting
/// trusted host paths (and repository tree paths — see Candidates/RepositoryPathPolicy) to
/// printable ASCII is the v1 fail-closed answer: any non-ASCII character is rejected BEFORE
/// any filesystem access.
/// </summary>
internal static class TrustedPathValidation
{
    /// <summary>Well-known generic/shared locations a Tenninety-managed root must never be:
    /// the trusted host must own and control the root, not share a system directory.</summary>
    private static readonly HashSet<string> SharedRootDenyList = new(StringComparer.Ordinal)
    {
        "/", "/tmp", "/var", "/var/tmp", "/var/run", "/run", "/dev", "/home", "/root",
        "/etc", "/usr", "/boot", "/proc", "/sys", "/opt", "/srv", "/mnt", "/media",
        "/lost+found", "/Applications", "/private/tmp", "/private/var",
    };

    /// <summary>Shape validation for every trusted input path: nonblank, absolute, canonical
    /// (no NUL/backslash/colon, no empty/'.'/'..' segments — which also rules out trailing
    /// slashes and double slashes) and PRINTABLE ASCII ONLY (see the type summary: the
    /// invariant-globalization runtime cannot validate Unicode normalization, so non-ASCII
    /// characters fail closed before any filesystem access). Relative inputs are rejected
    /// before any filesystem call could reinterpret them; no host <see cref="Path"/> API is
    /// used for the decision.</summary>
    public static string ValidateAbsoluteShape(string? path, string what)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException(
                $"the {what} must be a non-empty absolute directory path.");
        if (path.Contains('\0') || path.Contains('\\') || path.Contains(':'))
            throw new InvalidOperationException(
                $"the {what} must not contain NUL bytes, backslashes or colons: rooted syntax " +
                "(POSIX absolute, Windows drive, UNC, device or stream forms) is rejected " +
                "independently of the host operating system.");
        if (!path.StartsWith('/'))
            throw new InvalidOperationException(
                $"the {what} must be an absolute path; relative paths are rejected before " +
                "any filesystem access.");
        if (!path.All(c => c >= 0x20 && c <= 0x7E))
            throw new InvalidOperationException(
                $"the {what} must contain only printable ASCII characters: this runtime " +
                "cannot validate Unicode normalization (invariant globalization), so every " +
                "non-ASCII path fails closed before any filesystem access.");
        var segments = path.Split('/');
        if (segments.Skip(1).Any(s => s.Length == 0 || s is "." or ".."))
            throw new InvalidOperationException(
                $"the {what} must be canonical: empty, '.' and '..' segments (including " +
                "traversal and trailing slashes) are rejected.");
        return path;
    }

    /// <summary>Walks every component from the filesystem root down to and including the given
    /// directory. Each must exist as a real directory and none may be a symlink/reparse point:
    /// an ancestor link could redirect the location and hide its real path.</summary>
    public static void EnsureRealDirectoryChain(string path, string what)
    {
        var current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (!Directory.Exists(current))
                throw new InvalidOperationException(
                    $"the {what} does not exist or is not a directory.");
            if (IsReparsePoint(current))
                throw new InvalidOperationException(
                    $"the {what} must be reachable through real directories only: a " +
                    "symlink/reparse point above or at it could hide its real location.");
        }
    }

    /// <summary>Resolves a validated-shape absolute directory path to its physical location by
    /// walking components top-down and following any symlink/reparse point to its final
    /// target. The result is what all containment and overlap comparisons use.</summary>
    public static string ResolvePhysicalDirectory(string path, string what)
    {
        var current = "";
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            DirectoryInfo info;
            try
            {
                info = new DirectoryInfo(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                throw new InvalidOperationException(
                    $"the {what} could not be inspected safely on the filesystem.");
            }
            if (!info.Exists)
                throw new InvalidOperationException(
                    $"the {what} does not exist or is not a directory.");
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            FileSystemInfo resolved;
            try
            {
                resolved = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new InvalidOperationException(
                        $"the {what} resolves through an unresolvable link.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException
                                       or InvalidOperationException)
            {
                throw new InvalidOperationException(
                    $"the {what} could not be resolved to its physical location.");
            }
            if (!Directory.Exists(resolved.FullName))
                throw new InvalidOperationException(
                    $"the {what} resolves through a link to something that is not a directory.");
            current = resolved.FullName.TrimEnd('/');
        }
        return current.Length == 0 ? "/" : current;
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                   info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            throw new InvalidOperationException(
                "the sandbox paths could not be inspected safely on the filesystem.");
        }
    }

    public static string NormalizeHome()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home) || !home.StartsWith('/')) return "";
        return home.TrimEnd('/');
    }

    public static void EnsureNotSharedRoot(string root)
    {
        if (SharedRootDenyList.Contains(root))
            throw new InvalidOperationException(
                "the managed sandbox workspace root must be a directory dedicated to " +
                "Tenninety: generic shared locations such as the filesystem root or /tmp " +
                "itself are never accepted.");
    }
}
