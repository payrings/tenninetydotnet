using Tenninety.Execution.Candidates;

namespace Tenninety.Execution.Coding;

/// <summary>Fail-closed path policy for pinned OpenCode 1.18.29. Its project-discovery flags
/// suppress some resources, but live-image testing shows they do not reliably prevent every
/// project plugin execution path during <c>run</c>.</summary>
internal static class OpenCodeCandidatePolicy
{
    public const string PinnedVersion = "1.18.29";

    public static void Validate(IReadOnlyList<GitTreeEntry> entries)
    {
        var unsafePaths = entries
            .Select(entry => entry.Path)
            .Where(IsDiscoverableControlPlanePath)
            .Take(6)
            .ToList();
        if (unsafePaths.Count == 0) return;

        var shown = unsafePaths.Take(5).Select(path =>
            Core.Security.Sanitizer.SanitizeDiagnostic(path, 512));
        var suffix = unsafePaths.Count > 5 ? "; additional matching paths omitted" : "";
        throw new OpenCodeCandidateConfigurationException(
            $"OpenCode {PinnedVersion} cannot reliably isolate candidate project control-plane " +
            "configuration; refusing to launch OpenCode for: " +
            string.Join(", ", shown) + suffix + ".");
    }

    private static bool IsDiscoverableControlPlanePath(string path)
    {
        if (path is "opencode.json" or "opencode.jsonc" ||
            path is ".opencode" || path.StartsWith(".opencode/", StringComparison.Ordinal))
            return true;

        var slash = path.LastIndexOf('/');
        var name = slash < 0 ? path : path[(slash + 1)..];
        if (name is "AGENTS.md" or "CLAUDE.md" or "CONTEXT.md") return true;

        return (path.StartsWith(".agents/skills/", StringComparison.Ordinal) ||
                path.StartsWith(".claude/skills/", StringComparison.Ordinal)) &&
               name == "SKILL.md";
    }
}

internal sealed class OpenCodeCandidateConfigurationException(string message)
    : Exception(message);
