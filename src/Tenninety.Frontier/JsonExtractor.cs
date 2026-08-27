using System.Text.Json;

namespace Tenninety.Frontier;

/// <summary>Tolerant extraction of the first balanced JSON object from an LLM response (fences, prose, etc.).</summary>
public static class JsonExtractor
{
    public static string ExtractFirstJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("model returned an empty response.");

        var sawOpeningBrace = false;
        var start = -1;
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (start < 0)
            {
                if (c != '{') continue;
                sawOpeningBrace = true;
                start = i;
                depth = 1;
                inString = false;
                escaped = false;
                continue;
            }

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"')
            {
                inString = true;
                continue;
            }
            if (c == '{') depth++;
            if (c != '}' || --depth != 0) continue;

            var candidate = text[start..(i + 1)];
            try
            {
                using var _ = JsonDocument.Parse(candidate, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                return candidate;
            }
            catch (JsonException)
            {
                // Do not retry braces nested inside a rejected region: a malformed wrapper
                // must not expose a small, valid safety fragment as the model response.
                start = -1;
            }
        }

        if (!sawOpeningBrace)
            throw new InvalidOperationException($"no JSON object found in model response: {Truncate(text)}");
        throw new InvalidOperationException($"no valid JSON object found in model response: {Truncate(text)}");
    }

    private static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…";
}
