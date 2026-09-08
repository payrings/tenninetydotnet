using System.Runtime.CompilerServices;
using System.Text.Json;
using Tenninety.Core.Stores;
using Tenninety.Execution.Sandbox;

[assembly: InternalsVisibleTo("Tenninety.Tests")]

namespace Tenninety.Execution.Testing;

/// <summary>
/// Bounded, NON-EXECUTING structural prerequisite check for the restricted Restore phase. It
/// runs BEFORE any Restore or Tester Docker inspection, probe, session or container creation
/// (the caller materializes the candidate first — a local, Docker-free operation — so this
/// scan always sees the exact candidate content).
///
/// Restore always runs the FIXED <c>dotnet restore --locked-mode</c> command, which requires a
/// committed <c>packages.lock.json</c> for EVERY restored project and can never introduce a
/// newly selected package. Two different questions are deliberately separated:
///
///  - STRUCTURAL PREREQUISITES (here, cheap and Docker-free): every applicable project file
///    is located deterministically and must carry a committed lock file that is a bounded,
///    duplicate-free, structurally sound lock document. Missing, malformed, duplicated,
///    over-limit or structurally unexpected lock data fails with a controlled explanation
///    naming the owning project — never with a silent skip.
///  - LOCK CURRENCY (authoritative, in the Restore container): only the locked restore itself
///    can establish that a lock file is CURRENT for its project; a structurally perfect lock
///    file whose content no longer matches the project graph fails there (locked mode refuses
///    stale locks), which surfaces as the controlled Restore-exit gate failure.
///
/// Properties:
///  - no process is started and no MSBuild/restore code runs;
///  - traversal is depth-bounded, entry-count-bounded, project-count-bounded and
///    deterministic (ordinal-sorted); skipping .git/.tenninety/bin/obj/node_modules; directory
///    reparse points are never followed and the root itself must be a real directory;
///    EXHAUSTING any bound is a reported failure — content is never silently skipped;
///  - lock files are read through the trusted no-follow regular-file reader with a bounded
///    stream of at most <see cref="MaxLockFileBytes"/> + 1 bytes: the size decision is made on
///    the bytes actually read (never a length probe followed by an unbounded read), and the
///    whole-file identity/timestamp verification still runs for files within the bound;
///  - a lock file must be valid JSON (bounded depth, duplicate-field rejection, bounded
///    property names) whose root object carries a supported numeric format version and whose
///    target/dependency structure has the non-null fields emitted for each dependency type;
///  - restore TARGETS are selected explicitly (one canonical solution; otherwise every
///    project, or bounded solutions when no projects exist) so the fixed restore command
///    never depends on the working directory's implicit solution inference.
/// </summary>
public static class RestorePrerequisiteValidator
{
    public const int MaxRecursionDepth = 12;
    public const int MaxProjectsExamined = 256;
    public const int MaxEntriesExamined = 200_000;
    public const long MaxLockFileBytes = 4 * 1024 * 1024;
    public const int MaxRestoreTargets = MaxProjectsExamined;

    public static readonly string[] SkippedDirectoryNames =
    [".git", ".tenninety", "bin", "obj", "node_modules"];

    public static readonly string[] ProjectFileExtensions =
    [".csproj", ".fsproj", ".vbproj"];

    public static readonly string[] SolutionFileExtensions = [".sln", ".slnx"];

    public const string LockFileName = "packages.lock.json";

    /// <summary>The result of the bounded structural scan: a complete list of CONTROLLED
    /// failure explanations (bounded relative paths, no file content) and the explicit
    /// container-relative restore targets for the fixed locked restore command. A result is
    /// valid only when there are no failures and at least one target.</summary>
    public sealed record RestorePrerequisites(
        IReadOnlyList<string> Failures,
        IReadOnlyList<string> RestoreTargets)
    {
        public bool IsValid => Failures.Count == 0 && RestoreTargets.Count > 0;
    }

    public static RestorePrerequisites Validate(string root)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Fail(["restore prerequisite check: the candidate source directory is missing."]);
        if (TrustedPathValidation.IsReparsePoint(root))
            return Fail(["restore prerequisite check: the candidate source directory is not a " +
                         "real directory (redirect refused)."]);

        var entriesExamined = 0;
        var projectsExamined = 0;
        var solutions = new List<string>();
        var projects = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            var depth = CountSegmentsBelowRoot(root, directory);

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory)
                    .OrderBy(p => p, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException)
            {
                failures.Add($"restore prerequisite check: directory '{Relative(root, directory)}' " +
                             $"could not be listed ({ex.GetType().Name}).");
                continue;
            }

            foreach (var entry in entries)
            {
                if (++entriesExamined > MaxEntriesExamined)
                    return Fail(["restore prerequisite check: the candidate contains more than " +
                                 $"{MaxEntriesExamined} file-system entries; refusing unbounded " +
                                 "examination instead of silently skipping content."]);
                string fileName;
                try { fileName = Path.GetFileName(entry); }
                catch (ArgumentException) { continue; }

                if (Directory.Exists(entry))
                {
                    if (SkippedDirectoryNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                        continue;
                    if (TrustedPathValidation.IsReparsePoint(entry)) continue;
                    if (depth >= MaxRecursionDepth)
                    {
                        failures.Add(
                            $"restore prerequisite check: directory '{Relative(root, entry)}' " +
                            $"exceeds the bounded scan depth of {MaxRecursionDepth} levels; " +
                            "refusing unbounded examination instead of silently skipping content.");
                        continue;
                    }
                    pending.Push(entry);
                    continue;
                }

                if (IsSolutionFile(fileName)) solutions.Add(entry);
                if (!IsProjectFile(fileName)) continue;
                if (TrustedPathValidation.IsReparsePoint(entry)) continue;
                if (++projectsExamined > MaxProjectsExamined)
                    return Fail(["restore prerequisite check: more than " +
                                 $"{MaxProjectsExamined} project files exist in the candidate; " +
                                 "refusing unbounded examination instead of silently skipping " +
                                 "content."]);
                projects.Add(entry);
                ValidateProject(root, entry, failures);
            }
        }

        if (projects.Count == 0 && solutions.Count > MaxRestoreTargets)
            failures.Add(
                $"restore prerequisite check: more than {MaxRestoreTargets} solution files " +
                "exist without any discovered project files; no bounded explicit restore " +
                "target set can be selected.");

        if (failures.Count > 0 || (projects.Count == 0 && solutions.Count == 0))
        {
            if (failures.Count == 0)
                failures.Add(
                    "restore prerequisite check: no project or solution files were found in " +
                    "the candidate; the fixed locked restore command has no explicit target.");
            return new RestorePrerequisites(failures, []);
        }

        var restoreTargets = SelectRestoreTargets(root, solutions, projects);
        if (restoreTargets.Count == 0)
            return Fail(["restore prerequisite check: no bounded explicit restore target could " +
                         "be selected."]);
        return new RestorePrerequisites(failures, restoreTargets);

        static RestorePrerequisites Fail(List<string> list) => new(list, []);
    }

    /// <summary>Deterministic explicit restore targets: exactly one discovered solution file
    /// wins (it is the canonical restore root); with multiple solutions, every discovered
    /// project is restored, or every solution when there are no projects. Container-relative,
    /// ordinal-sorted, never a working-directory inference.</summary>
    private static IReadOnlyList<string> SelectRestoreTargets(
        string root, List<string> solutions, List<string> projects)
    {
        var targets = solutions.Count == 1
            ? [solutions[0]]
            : projects.Count > 0 ? projects : solutions;
        return targets
            .Select(path => ContainerWorkspacePath(root, path))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Converts a discovered host path into its exact container-side /workspace path
    /// (the workspace bind is mounted at exactly <see cref="SandboxPolicy.ContainerWorkspacePath"/>).</summary>
    private static string ContainerWorkspacePath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path)
            .Replace('\\', '/')
            .TrimStart('/');
        return SandboxPolicy.ContainerWorkspacePath + "/" + relative;
    }

    private static void ValidateProject(string root, string projectPath, List<string> failures)
    {
        var relative = Relative(root, projectPath);
        var lockPath = Path.Combine(Path.GetDirectoryName(projectPath)!, LockFileName);
        if (!File.Exists(lockPath))
        {
            failures.Add(
                $"restore prerequisite check: project '{relative}' has no '{LockFileName}'. " +
                "Restore always runs 'dotnet restore --locked-mode', which requires a committed " +
                "lock file for every restored project; regenerate and review the lock closure " +
                "with an operator-controlled process before restoring.");
            return;
        }

        if (TrustedPathValidation.IsReparsePoint(lockPath))
        {
            failures.Add($"restore prerequisite check: lock file for '{relative}' is not a " +
                         "regular file (redirect refused).");
            return;
        }

        try
        {
            // Bounded stream read: at most MaxLockFileBytes + 1 bytes are EVER read, so the
            // size decision happens on the bytes themselves (the file may change between a
            // length probe and a full read — that race cannot produce an unbounded read).
            using var opened = TrustedFileReader.OpenRegularFileNoFollow(lockPath);
            var buffer = new byte[MaxLockFileBytes + 1];
            long totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = RandomAccess.Read(opened.Handle, buffer.AsSpan((int)totalRead), totalRead);
                if (read == 0) break;
                totalRead += read;
            }
            if (totalRead > MaxLockFileBytes)
            {
                failures.Add($"restore prerequisite check: lock file for '{relative}' exceeds " +
                             $"{MaxLockFileBytes} bytes; refusing to trust it.");
                return;
            }
            opened.VerifyUnchanged(totalRead);

            var json = buffer.AsSpan(0, (int)totalRead).ToArray();
            if (!IsWellFormedLockFile(json, out var structuralProblem))
            {
                failures.Add($"restore prerequisite check: lock file for '{relative}' is not a " +
                             $"well-formed packages.lock.json document ({structuralProblem}); " +
                             "Restore in locked mode cannot use it.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException or InvalidOperationException
                                   or ObjectDisposedException)
        {
            failures.Add($"restore prerequisite check: lock file for '{relative}' could not be " +
                         $"read and verified ({ex.GetType().Name}).");
        }
    }

    /// <summary>Structural lock-file check: strict shape (bounded depth, duplicate-field
    /// rejection, bounded property names, valid JSON) plus the packages.lock.json structure:
    /// a root object, a supported numeric "version", and its target/dependency layout.
    /// Dependency fields are checked according to type; locked restore remains authoritative
    /// for graph semantics and currency. Content values are never echoed anywhere.</summary>
    internal static bool IsWellFormedLockFile(byte[] json, out string problem)
    {
        problem = "";
        try
        {
            StrictJsonIngestion.EnsureStrictShape(
                json, MaxLockFileBytes, "packages.lock.json", maxDepth: 24);
        }
        catch (InvalidOperationException ex)
        {
            problem = ex.Message.Contains("duplicate field", StringComparison.Ordinal)
                ? "duplicate fields are rejected"
                : ex.Message.Contains("exceeds its persisted size bound", StringComparison.Ordinal)
                    ? "size bound exceeded"
                    : ex.Message.Contains("nesting exceeds", StringComparison.Ordinal)
                        ? "nesting depth exceeded"
                        : ex.Message.Contains("property name longer", StringComparison.Ordinal)
                            ? "property name length exceeded"
                            : "malformed JSON";
            return false;
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 24,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            problem = "the root must be a JSON object";
            return false;
        }
        if (!root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var versionValue) || versionValue is < 1 or > 3)
        {
            problem = "a supported numeric 'version' member (1, 2, or 3) is required";
            return false;
        }

        if (ContainsNull(root))
        {
            problem = "null members are rejected";
            return false;
        }

        if (versionValue < 3)
        {
            if (!root.TryGetProperty("dependencies", out var dependencies) ||
                dependencies.ValueKind != JsonValueKind.Object)
            {
                problem = "a 'dependencies' object member is required";
                return false;
            }

            foreach (var target in dependencies.EnumerateObject())
            {
                if (target.Value.ValueKind != JsonValueKind.Object)
                {
                    problem = $"dependencies target '{BoundedName(target.Name)}' must be an object";
                    return false;
                }
                if (!ValidateDependencies(target.Value, target.Name, out problem))
                    return false;
            }
            return true;
        }

        foreach (var target in root.EnumerateObject())
        {
            if (target.NameEquals("version")) continue;
            if (target.Value.ValueKind != JsonValueKind.Object)
            {
                problem = $"dependencies target '{BoundedName(target.Name)}' must be an object";
                return false;
            }
            if (!target.Value.TryGetProperty("framework", out var framework) ||
                framework.ValueKind != JsonValueKind.String)
            {
                problem = $"dependencies target '{BoundedName(target.Name)}' requires a string " +
                          "'framework' member";
                return false;
            }
            if (!target.Value.TryGetProperty("dependencies", out var targetDependencies) ||
                targetDependencies.ValueKind != JsonValueKind.Object)
            {
                problem = $"dependencies target '{BoundedName(target.Name)}' requires a " +
                          "'dependencies' object member";
                return false;
            }
            if (!ValidateDependencies(targetDependencies, target.Name, out problem))
                return false;
        }
        return true;
    }

    private static bool ValidateDependencies(
        JsonElement dependencies, string targetName, out string problem)
    {
        foreach (var dependency in dependencies.EnumerateObject())
        {
            var prefix = $"dependency '{BoundedName(dependency.Name)}' in target " +
                         $"'{BoundedName(targetName)}'";
            if (dependency.Value.ValueKind != JsonValueKind.Object)
            {
                problem = prefix + " must be an object";
                return false;
            }
            if (!dependency.Value.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                problem = prefix + " requires a string 'type' member";
                return false;
            }

            var typeName = type.GetString();
            var isDirect = string.Equals(typeName, "Direct", StringComparison.OrdinalIgnoreCase);
            var isTransitive = string.Equals(
                typeName, "Transitive", StringComparison.OrdinalIgnoreCase);
            var isProject = string.Equals(typeName, "Project", StringComparison.OrdinalIgnoreCase);
            var isCentralTransitive = string.Equals(
                typeName, "CentralTransitive", StringComparison.OrdinalIgnoreCase);
            if (!isDirect && !isTransitive && !isProject && !isCentralTransitive)
            {
                problem = prefix + " has an unknown 'type' member";
                return false;
            }

            if ((isDirect || isCentralTransitive) &&
                !HasStringMember(dependency.Value, "requested", prefix, out problem))
                return false;
            if (!isProject &&
                (!HasStringMember(dependency.Value, "resolved", prefix, out problem) ||
                 !HasStringMember(dependency.Value, "contentHash", prefix, out problem)))
                return false;

            foreach (var optionalString in new[] { "requested", "resolved", "contentHash" })
            {
                if (dependency.Value.TryGetProperty(optionalString, out var member) &&
                    member.ValueKind != JsonValueKind.String)
                {
                    problem = prefix + $" has a non-string '{optionalString}' member";
                    return false;
                }
            }
            if (dependency.Value.TryGetProperty("dependencies", out var nested) &&
                nested.ValueKind != JsonValueKind.Object)
            {
                problem = prefix + " has a non-object 'dependencies' member";
                return false;
            }
        }
        problem = "";
        return true;
    }

    private static bool HasStringMember(
        JsonElement dependency, string memberName, string prefix, out string problem)
    {
        if (!dependency.TryGetProperty(memberName, out var member) ||
            member.ValueKind != JsonValueKind.String)
        {
            problem = prefix + $" requires a string '{memberName}' member";
            return false;
        }
        problem = "";
        return true;
    }

    private static bool ContainsNull(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(ContainsNull);
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property => ContainsNull(property.Value));
        return false;
    }

    /// <summary>Property names are already bounded by the strict shape walk; the final bound
    /// keeps any future change honest.</summary>
    private static string BoundedName(string name) =>
        name.Length <= 100 ? name : name[..100] + "…";

    private static bool IsProjectFile(string fileName) =>
        ProjectFileExtensions.Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsSolutionFile(string fileName) =>
        SolutionFileExtensions.Any(extension =>
            fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string Relative(string root, string path)
    {
        var relative = path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal)
            ? path[(root.Length + 1)..]
            : Path.GetFileName(path);
        return relative.Length <= 200 ? relative : relative[..200];
    }

    private static int CountSegmentsBelowRoot(string root, string path)
    {
        var relative = path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal)
            ? path[(root.Length + 1)..]
            : "";
        return string.IsNullOrEmpty(relative) ? 0 : relative.Split('/').Length;
    }
}
