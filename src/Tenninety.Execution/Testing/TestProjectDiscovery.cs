using Microsoft.Win32.SafeHandles;
using System.Xml;
using System.Xml.Linq;

namespace Tenninety.Execution.Testing;

/// <summary>
/// Bounded, NON-EXECUTING discovery of a test project inside one disposable directory tree
/// (the freshly materialized candidate workspace — never the authoritative checkout).
///
/// Security/robustness properties:
///  - no process is started and no MSBuild/restore/import/target/project code runs;
///  - traversal is depth-bounded, count-bounded, deterministic (ordinal-sorted entries) and
///    skips .git/.tenninety/bin/obj/node_modules entirely;
///  - the discovery ROOT itself must be a real directory: a root replaced by a
///    symlink/reparse point is rejected (no evidence), and directory reparse points below it
///    are never followed, so discovery cannot be redirected outside the disposable tree;
///  - each examined project file is opened through the trusted no-follow regular-file reader
///    (<see cref="Sandbox.TrustedFileReader"/>): FIFOs, sockets, devices, directories and
///    symlinks are never opened as readable project files, and the opened descriptor's
///    identity, size and timestamps are re-verified after reading (change fails closed). On a
///    platform without that reader's regular-file proof, discovery fails closed (no
///    evidence) instead of opening the file as an ordinary stream;
///  - bytes stay bounded (files over the limit are never examined), and a regular project
///    file EXACTLY at the byte limit still parses;
///  - XML is parsed with DTD processing prohibited and no external resolver; malformed or
///    unreadable XML is NOT evidence of a test project;
///  - a solution file alone is never evidence that tests exist.
///
/// Recognition rules are preserved from the legacy host tester: an
/// &lt;IsTestProject&gt;true&lt;/IsTestProject&gt; marker, or a PackageReference
/// Include/Update attribute referencing xunit, nunit or MSTest (including nested projects).
/// </summary>
public static class TestProjectDiscovery
{
    public const int MaxRecursionDepth = 12;
    public const int MaxProjectFilesExamined = 256;
    public const long MaxProjectFileBytes = 1_048_576;

    public static readonly string[] SkippedDirectoryNames =
    [".git", ".tenninety", "bin", "obj", "node_modules"];

    /// <summary>
    /// Returns the bounded path of the first recognized test project, or null when the tree
    /// contains no evidence of a runnable suite.
    /// </summary>
    public static string? FindTestProject(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;
        if (IsReparsePoint(root))
            return null; // a redirected discovery root is never examined

        var projectFilesExamined = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var depth = CountSegmentsBelowRoot(root, directory);
            if (depth > MaxRecursionDepth) continue;

            string[] entries;
            try
            {
                // Deterministic ordinal ordering; failures to list are not evidence either.
                entries = Directory.GetFileSystemEntries(directory)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                string fileName;
                try { fileName = Path.GetFileName(entry); }
                catch (ArgumentException) { continue; }

                if (Directory.Exists(entry))
                {
                    if (SkippedDirectoryNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (IsReparsePoint(entry)) continue;
                    if (depth < MaxRecursionDepth) pending.Push(entry);
                    continue;
                }

                if (!fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (IsReparsePoint(entry)) continue;

                if (projectFilesExamined >= MaxProjectFilesExamined) return null;
                projectFilesExamined++;
                if (LooksLikeTestProject(entry)) return entry;
            }
        }
        return null;
    }

    /// <summary>Parses one bounded, PROVEN-REGULAR project file with hostile-input XML
    /// settings and applies the preserved recognition rules. Any failure — including the
    /// inability to prove the entry is a regular file — means "no evidence".</summary>
    private static bool LooksLikeTestProject(string projectPath)
    {
        try
        {
            // Trusted no-follow open: O_PATH inspection + fstat regular-file proof + identity
            // double-open. A FIFO/socket/device/directory/symlink (or an unprovable entry on
            // an unsupported platform) fails closed here without ever blocking or executing.
            using var opened = Sandbox.TrustedFileReader.OpenRegularFileNoFollow(projectPath);
            if (opened.Length > MaxProjectFileBytes)
                return false; // over-limit files are never examined

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = false,
                IgnoreWhitespace = true,
            };
            long totalRead;
            XDocument project;
            using (var bounded = new BoundedRegularFileStream(opened.Handle, MaxProjectFileBytes))
            {
                using var reader = XmlReader.Create(bounded, settings);
                project = XDocument.Load(reader);
                // Drain the remaining bytes so identity verification can prove the WHOLE
                // opened file was streamed (exactly-at-limit files included).
                var drain = new byte[81920];
                while (bounded.Read(drain, 0, drain.Length) > 0) { }
                totalRead = bounded.TotalBytesRead;
            }
            opened.VerifyUnchanged(totalRead);

            var isTestProject = project.Descendants()
                .Any(e => e.Name.LocalName == "IsTestProject" &&
                          bool.TryParse(e.Value.Trim(), out var value) && value);
            var hasTestPackage = project.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "")
                .Any(IsTestPackage);
            return isTestProject || hasTestPackage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or XmlException
                                   or ArgumentException or InvalidOperationException
                                   or ObjectDisposedException)
        {
            // Malformed/inaccessible/not-provably-regular project files do not prove that a
            // runnable suite exists: no evidence, fail closed.
            return false;
        }
    }

    private static bool IsTestPackage(string package) =>
        package.Equals("xunit", StringComparison.OrdinalIgnoreCase) ||
        package.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase) ||
        package.Equals("nunit", StringComparison.OrdinalIgnoreCase) ||
        package.StartsWith("nunit.", StringComparison.OrdinalIgnoreCase) ||
        package.StartsWith("mstest.", StringComparison.OrdinalIgnoreCase);

    private static bool IsReparsePoint(string path)
    {
        try
        {
            // Reuse the existing trusted helper (Tenninety.Execution.Sandbox).
            return Sandbox.TrustedPathValidation.IsReparsePoint(path);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                   or UnauthorizedAccessException)
        {
            return true; // uninspectable -> treat as unsafe, never follow
        }
    }

    private static int CountSegmentsBelowRoot(string root, string path)
    {
        var relative = path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal)
            ? path[(root.Length + 1)..]
            : "";
        return string.IsNullOrEmpty(relative) ? 0 : relative.Split('/').Length;
    }

    /// <summary>Non-owning, non-seeking stream over an opened regular-file descriptor: reads
    /// are capped at the discovery size bound and EOF is reported normally (a file exactly at
    /// the bound reads to its end and parses; the cap is only a belt-and-braces stop for a
    /// file that grew after its size was proven — identity verification then fails closed).
    /// The descriptor is NOT disposed here; the trusted reader owns it.</summary>
    private sealed class BoundedRegularFileStream(SafeFileHandle handle, long maxBytes) : Stream
    {
        private long _position;

        public long TotalBytesRead => _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count == 0) return 0;
            var boundRemaining = maxBytes - _position;
            if (boundRemaining <= 0) return 0;
            if (count > boundRemaining) count = (int)boundRemaining;
            if (count > buffer.Length - offset) count = buffer.Length - offset;
            var read = RandomAccess.Read(handle, buffer.AsSpan(offset, count), _position);
            if (read > 0) _position += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
