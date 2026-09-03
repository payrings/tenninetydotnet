using System.Text.Json.Serialization;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Trusted value object for the Tenninety-managed sandbox workspace root. It can only be
/// produced by <see cref="Create"/>, which fails closed unless the root is a nonblank,
/// canonical, absolute printable-ASCII POSIX directory path with no NUL/backslash/colon and no
/// empty/'.'/'..' segments, is not the filesystem root or the user's home directory, is not a
/// generic shared location such as /tmp itself, exists as a real directory, is not
/// group-writable or world-writable, and has NO symlink/reparse point above or at it — so the
/// physical location cannot be hidden or redirected. The validated physical location is what
/// all subsequent path construction must use.
///
/// Ownership note: the denylist and permission checks prove the root is dedicated and
/// locked down, but exact OS-user ownership verification is a trusted precondition deferred
/// to the later root-lifecycle/preflight work; this object does not claim to prove that the
/// process owner owns the directory.
///
/// The value is excluded from serialization and <see cref="ToString"/> is sanitized.
/// </summary>
public sealed class ValidatedManagedRootPath
{
    private ValidatedManagedRootPath(string value) => _value = value;

    private readonly string _value;

    /// <summary>The validated physical absolute root path. Excluded from serialization.</summary>
    [JsonIgnore]
    public string Value => _value;

    /// <summary>Sanitized on purpose: never leak host paths into logs.</summary>
    public override string ToString() => "[validated managed root]";

    public static ValidatedManagedRootPath Create(string? managedRoot)
    {
        var root = TrustedPathValidation.ValidateAbsoluteShape(
            managedRoot, "managed sandbox workspace root");
        if (root == "/")
            throw new InvalidOperationException(
                "the managed sandbox workspace root must never be '/'.");
        var home = TrustedPathValidation.NormalizeHome();
        if (home.Length > 0 && root == home)
            throw new InvalidOperationException(
                "the managed sandbox workspace root must never be the user's home directory.");
        TrustedPathValidation.EnsureNotSharedRoot(root);
        TrustedPathValidation.EnsureRealDirectoryChain(root, "managed sandbox workspace root");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(root);
            if (mode.HasFlag(UnixFileMode.GroupWrite) || mode.HasFlag(UnixFileMode.OtherWrite))
                throw new InvalidOperationException(
                    "the managed sandbox workspace root must not be group-writable or " +
                    "world-writable: the trusted host must own and control the root.");
        }
        // The chain walk rejected every reparse point above and at the root, so the lexical
        // path is already the physical location; resolve anyway as defense in depth.
        var physical = TrustedPathValidation.ResolvePhysicalDirectory(
            root, "managed sandbox workspace root");
        if (physical != root)
            throw new InvalidOperationException(
                "the managed sandbox workspace root could not be proven to be its own " +
                "physical location.");
        return new ValidatedManagedRootPath(physical);
    }
}
