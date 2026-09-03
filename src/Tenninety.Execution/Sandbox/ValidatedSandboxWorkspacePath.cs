using System.Text.Json.Serialization;

namespace Tenninety.Execution.Sandbox;

/// <summary>
/// Trusted value object for the disposable host directory that gets mounted into a sandbox
/// container at /workspace. It can only be produced by <see cref="Create"/>, which REQUIRES the
/// authoritative repository to be known and validated — no mountable workspace value can ever
/// exist before the repository is known (a pre-repository representation, if ever needed, must
/// be a different type that <see cref="SandboxSpec"/> cannot accept).
///
/// <see cref="Create"/> fails closed unless:
///
/// <list type="bullet">
/// <item>all three inputs (managed root, candidate, authoritative repository) are nonblank,
/// absolute, canonical printable-ASCII POSIX paths — relative inputs are rejected before any
/// filesystem or <see cref="Path"/> call could reinterpret them, rooted syntax from any
/// operating system (drive, UNC, device, stream forms) is unreachable because NUL, backslash
/// and colon are all rejected, and non-ASCII characters fail closed because the
/// invariant-globalization runtime cannot validate Unicode normalization;</item>
/// <item>every filesystem component from the filesystem root down to the managed root is a real
/// directory and none is a symlink/reparse point — an ancestor link must never hide the root's
/// real location;</item>
/// <item>the candidate exists as a directory and contains no symlink/reparse point in any
/// component from the managed root down to the candidate itself;</item>
/// <item>the authoritative repository exists and is resolved to its PHYSICAL location (a
/// symlink alias can never bypass overlap detection);</item>
/// <item>the physical candidate is strictly beneath the physical managed root (exact
/// path-segment containment, never a prefix string match), and does not equal, contain, or sit
/// inside the physical repository.</item>
/// </list>
///
/// The path value is excluded from serialization, <see cref="ToString"/> is sanitized, and no
/// error message contains any input path, so nothing can leak into agent/session-facing
/// context or logs. Creation proves properties at validation time; the later runtime MUST
/// re-validate immediately before mounting (TOCTOU on shared hosts is inherent and handled by
/// the mount step, not here). No candidate materialization happens here — this is a
/// control-plane value object only.
/// </summary>
public sealed class ValidatedSandboxWorkspacePath
{
    private ValidatedSandboxWorkspacePath(string value) => _value = value;

    private readonly string _value;

    /// <summary>The validated physical absolute host path. Excluded from serialization.</summary>
    [JsonIgnore]
    public string Value => _value;

    /// <summary>Sanitized on purpose: never leak the host scratch path into logs.</summary>
    public override string ToString() => "[validated sandbox workspace]";

    /// <summary>
    /// Validates the candidate against the managed workspace root and the mandatory
    /// authoritative repository, comparing physical filesystem locations. Throws
    /// <see cref="InvalidOperationException"/> with a precise, path-free reason on any violation.
    /// </summary>
    /// <param name="candidatePath">Disposable workspace directory to validate.</param>
    /// <param name="managedRoot">The Tenninety-managed workspace root; every sandbox workspace
    /// must live strictly beneath it.</param>
    /// <param name="authoritativeRepositoryPath">The authoritative checkout. Required: the
    /// repository must be known before any mountable workspace can be validated.</param>
    public static ValidatedSandboxWorkspacePath Create(
        string? candidatePath,
        string managedRoot,
        string authoritativeRepositoryPath)
    {
        // 1. Lexical shape of every input, before any filesystem access could reinterpret it.
        var root = TrustedPathValidation.ValidateAbsoluteShape(
            managedRoot, "managed workspace root");
        var candidate = TrustedPathValidation.ValidateAbsoluteShape(
            candidatePath, "sandbox workspace");
        var repository = TrustedPathValidation.ValidateAbsoluteShape(
            authoritativeRepositoryPath, "authoritative repository");

        // 2. The managed root must be reachable through real directories only: no symlink or
        //    reparse point above or at the root may hide its real location.
        TrustedPathValidation.EnsureRealDirectoryChain(root, "managed workspace root");
        var home = TrustedPathValidation.NormalizeHome();
        if (home.Length > 0 && root == home)
            throw new InvalidOperationException(
                "the managed sandbox workspace root must never be the user's home directory.");

        // 3. The candidate must exist.
        if (!Directory.Exists(candidate))
            throw new InvalidOperationException(
                "the sandbox workspace does not exist or is not a directory; create the " +
                "disposable attempt directory before validating it.");

        // 4. The authoritative repository must exist and be resolved to its physical location;
        //    a symlink alias must never bypass overlap detection.
        var physicalRepository = TrustedPathValidation.ResolvePhysicalDirectory(
            repository, "authoritative repository");

        // 5. Resolve the candidate physically as well.
        var physicalCandidate = TrustedPathValidation.ResolvePhysicalDirectory(
            candidate, "sandbox workspace");

        // 6. Exact path-segment containment on PHYSICAL locations.
        if (physicalCandidate == root ||
            !physicalCandidate.StartsWith(root + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the sandbox workspace must be strictly beneath the managed workspace root " +
                "(never the root itself): exact path-segment containment of the physical " +
                "locations is required.");

        // 7. No redirects between the managed root and the candidate. Physical containment
        //    above proves lexical containment (the root chain is link-free), so the lexical
        //    relative walk is well-defined.
        RejectReparsePointsBeneathRoot(root, candidate);

        // 8. Overlap with the authoritative repository on PHYSICAL locations, both directions.
        if (physicalCandidate == physicalRepository)
            throw new InvalidOperationException(
                "the sandbox workspace must never be the authoritative repository.");
        if (physicalCandidate.StartsWith(physicalRepository + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the sandbox workspace must never live inside the authoritative repository.");
        if (physicalRepository.StartsWith(physicalCandidate + "/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "the sandbox workspace must never contain the authoritative repository " +
                "(an ancestor of the repository is never a valid workspace).");

        return new ValidatedSandboxWorkspacePath(physicalCandidate);
    }

    /// <summary>Walks every component strictly beneath the managed root, up to and including
    /// the workspace directory itself, and rejects any symlink/reparse point so nothing along
    /// the path can redirect the mount outside the managed root or into the repository.</summary>
    private static void RejectReparsePointsBeneathRoot(string root, string candidate)
    {
        var relative = candidate[(root.Length + 1)..];
        var current = root;
        foreach (var segment in relative.Split('/'))
        {
            current = $"{current}/{segment}";
            if (!TrustedPathValidation.IsReparsePoint(current)) continue;
            throw new InvalidOperationException(
                "the sandbox workspace path must not contain symlink/reparse point components: " +
                "a link in the managed-root-to-directory path could redirect the mount outside " +
                "the managed root or into the authoritative repository.");
        }
    }
}
