using System.Text;
using System.Text.Json;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Execution.Reviewing;

public abstract record ReviewerResponse;

public sealed record ReviewerCommandResponse(string Command) : ReviewerResponse;

public sealed record ReviewerVerdictResponse(bool Passed, IReadOnlyList<string> Reasons)
    : ReviewerResponse;

/// <summary>Strict bounded JSON protocol for the host-controlled reviewer loop. Markdown,
/// duplicate/unknown fields, contradictory verdicts and trailing JSON are rejected.</summary>
public static class ReviewerProtocol
{
    public const int MaxCommandChars = 4096;
    public const int MaxReasons = 20;
    public const int MaxReasonChars = 512;

    public static ReviewerResponse Parse(string response, long maxUtf8Bytes)
    {
        if (response is null || Encoding.UTF8.GetByteCount(response) > maxUtf8Bytes)
            throw new ReviewerProtocolException(
                "the reviewer model response exceeded the configured bound.");
        var bytes = Encoding.UTF8.GetBytes(response);
        try
        {
            StrictJson.EnsureNoDuplicateFields(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid();
            if (!root.TryGetProperty("action", out var actionElement) ||
                actionElement.ValueKind != JsonValueKind.String)
                throw Invalid();
            return actionElement.GetString() switch
            {
                "run" => ParseCommand(root),
                "final" => ParseVerdict(root),
                _ => throw Invalid(),
            };
        }
        catch (ReviewerProtocolException) { throw; }
        catch (Exception)
        {
            throw Invalid();
        }
    }

    private static ReviewerCommandResponse ParseCommand(JsonElement root)
    {
        RequireExactProperties(root, "action", "command");
        if (!root.TryGetProperty("command", out var commandElement) ||
            commandElement.ValueKind != JsonValueKind.String)
            throw Invalid();
        var command = commandElement.GetString() ?? "";
        if (string.IsNullOrWhiteSpace(command) || command.Length > MaxCommandChars ||
            command.Contains('\0') || command.Any(char.IsControl))
            throw Invalid();
        return new ReviewerCommandResponse(command);
    }

    private static ReviewerVerdictResponse ParseVerdict(JsonElement root)
    {
        RequireExactProperties(root, "action", "verdict", "reasons");
        if (!root.TryGetProperty("verdict", out var verdictElement) ||
            verdictElement.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("reasons", out var reasonsElement) ||
            reasonsElement.ValueKind != JsonValueKind.Array)
            throw Invalid();
        var verdict = verdictElement.GetString();
        if (verdict is not ("PASS" or "FAIL") ||
            reasonsElement.GetArrayLength() > MaxReasons)
            throw Invalid();
        var reasons = new List<string>();
        foreach (var element in reasonsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String) throw Invalid();
            var reason = element.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > MaxReasonChars ||
                reason.Contains('\0') || reason.Any(char.IsControl))
                throw Invalid();
            reasons.Add(reason);
        }
        if (verdict == "PASS" && reasons.Count != 0 ||
            verdict == "FAIL" && reasons.Count == 0)
            throw Invalid();
        return new ReviewerVerdictResponse(verdict == "PASS", reasons.AsReadOnly());
    }

    private static void RequireExactProperties(JsonElement root, params string[] names)
    {
        var expected = names.ToHashSet(StringComparer.Ordinal);
        var actual = root.EnumerateObject().Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected)) throw Invalid();
    }

    private static ReviewerProtocolException Invalid() =>
        new("the reviewer returned malformed, unknown or contradictory protocol JSON.");
}

public sealed class ReviewerProtocolException : Exception
{
    public ReviewerProtocolException(string message) : base(message) { }
}

public sealed class ReviewerInfrastructureException : Exception
{
    public ReviewerInfrastructureException(string message) : base(message) { }
}
