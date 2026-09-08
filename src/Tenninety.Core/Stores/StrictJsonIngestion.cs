using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("Tenninety.Tests")]

namespace Tenninety.Core.Stores;

/// <summary>
/// Strict, bounded JSON ingestion for framework-owned contracts under .tenninety/ (config.json,
/// plan.json). These files are partially human-edited and plan.json is fully untrusted model
/// output, so ingestion is deliberately stricter than the shared serializer options:
///
///  - a hard file-size bound before any parse;
///  - a bounded nesting depth;
///  - duplicate-field rejection at EVERY object depth (System.Text.Json silently keeps the
///    last duplicate, which could turn a typo such as "provider_mod" into a silent default);
///  - unknown-member rejection at deserialization time (contract-specific options only — the
///    shared <see cref="Json.Options"/> keeps serving state migration and unrelated protocols);
///  - bounded property-name lengths and controlled error messages that name the owning field
///    or path without echoing file content.
///
/// Comments and trailing commas remain accepted for backward compatibility with the
/// JSONC-flavoured annotated examples.
/// </summary>
public static class StrictJsonIngestion
{
    /// <summary>Hard size bound for .tenninety/config.json (the framework config is small).</summary>
    public const long MaxConfigBytes = 1024 * 1024;

    /// <summary>Hard size bound for .tenninety/plan.json (plans are bounded by design).</summary>
    public const long MaxPlanBytes = 4 * 1024 * 1024;

    /// <summary>Maximum JSON nesting depth accepted for ingested contracts.</summary>
    public const int MaxDepth = 32;

    /// <summary>Maximum length of any JSON property name (typo/garbage guard).</summary>
    public const int MaxPropertyNameChars = 128;

    /// <summary>
    /// Race-conscious bounded read of a JSON contract file: reads AT MOST
    /// <paramref name="maxBytes"/> + 1 bytes from the open stream — the size decision is made
    /// on what is actually read, never on a length probe taken before reading (the file can
    /// change between a length check and a later full read). Throws the same controlled
    /// size-bound error as <see cref="EnsureStrictShape"/> when the stream yields more than
    /// <paramref name="maxBytes"/> bytes.
    /// </summary>
    public static byte[] ReadBounded(string path, long maxBytes, string contractLabel)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.SequentialScan);
        return ReadBounded(stream, maxBytes, contractLabel);
    }

    internal static byte[] ReadBounded(Stream stream, long maxBytes, string contractLabel)
    {
        if (maxBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var buffer = new MemoryStream(capacity: 4096);
        var chunk = new byte[8192];
        while (true)
        {
            var remaining = maxBytes - buffer.Length;
            var requested = remaining >= chunk.Length
                ? chunk.Length
                : checked((int)remaining + 1);
            var read = stream.Read(chunk, 0, requested);
            if (read == 0) break;
            if (buffer.Length + read > maxBytes)
                throw new InvalidOperationException(
                    $"{contractLabel} exceeds its persisted size bound of {maxBytes} bytes " +
                    "(the stream was cut at the bound; content withheld); refusing to parse.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Walks the raw JSON with a bounded <see cref="Utf8JsonReader"/> and rejects: over-limit
    /// input, nesting beyond <paramref name="maxDepth"/>, duplicate fields at any depth, and
    /// overlong property names. Throws <see cref="InvalidOperationException"/> with a message
    /// that names the contract and the offending path (bounded, no file content echoed).
    /// </summary>
    public static void EnsureStrictShape(
        byte[] json, long maxBytes, string contractLabel,
        int maxDepth = MaxDepth, bool allowTrailingCommas = true)
    {
        if (json.LongLength > maxBytes)
            throw new InvalidOperationException(
                $"{contractLabel} exceeds its persisted size bound of {maxBytes} bytes " +
                $"({json.LongLength} bytes); refusing to parse.");
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = allowTrailingCommas,
            CommentHandling = JsonCommentHandling.Skip,
            // One level of headroom so OUR bound produces the controlled, path-aware error
            // message instead of a generic parser failure.
            MaxDepth = maxDepth + 1,
        });
        // Path bookkeeping: names are pushed when encountered and popped when their value
        // completes, so errors name the owning object path (e.g. "sandbox.roles.coder")
        // without echoing any value content.
        var seen = new Stack<HashSet<string>>([]);
        var scopesArePropertyValues = new Stack<bool>();
        var path = new Stack<string>([]);
        var lastWasPropertyName = false;
        while (true)
        {
            try
            {
                if (!reader.Read()) break;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"{contractLabel} is not valid JSON at line {ex.LineNumber}, byte " +
                    $"position {ex.BytePositionInLine} (malformed input refused; content " +
                    "withheld).");
            }
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    if (seen.Count >= maxDepth)
                        throw new InvalidOperationException(
                            $"{contractLabel} nesting exceeds the maximum depth of {maxDepth} " +
                            $"levels at '{CurrentPath(path)}'.");
                    seen.Push(new HashSet<string>(StringComparer.Ordinal));
                    scopesArePropertyValues.Push(lastWasPropertyName);
                    lastWasPropertyName = false;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    seen.Pop();
                    // A nested object/array VALUE completes here: the property name that
                    // owned that value must leave the path now. `lastWasPropertyName` is
                    // always false at an End* token (the name was followed by its value),
                    // so relying on it would leak every nested-value owner into the path
                    // and misattribute later sibling-property errors. The flag pushed at
                    // Start* records whether this scope was a property value.
                    if (scopesArePropertyValues.Pop()) path.Pop();
                    lastWasPropertyName = false;
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString() ?? "";
                    if (name.Length > MaxPropertyNameChars)
                        throw new InvalidOperationException(
                            $"{contractLabel} has a property name longer than " +
                            $"{MaxPropertyNameChars} characters at '{CurrentPath(path)}'.");
                    if (!seen.Peek().Add(name))
                        throw new InvalidOperationException(
                            $"{contractLabel} contains the duplicate field '{name}' at " +
                            $"'{CurrentPath(path)}'; duplicate fields are rejected because " +
                            "last-one-wins could silently change the configured behavior.");
                    path.Push(name);
                    lastWasPropertyName = true;
                    break;
                default:
                    // Scalar value completes; the property name it belongs to leaves the path.
                    if (lastWasPropertyName) path.Pop();
                    lastWasPropertyName = false;
                    break;
            }
        }
    }

    /// <summary>
    /// Deserializes with contract-specific STRICT options: unknown members are rejected,
    /// nesting is bounded, and comments/trailing commas stay accepted. The shared
    /// <see cref="Json.Options"/> is deliberately untouched.
    /// </summary>
    public static T Deserialize<T>(
        byte[] json, string contractLabel, int maxDepth = MaxDepth)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(json, StrictOptions(maxDepth));
            return result ?? throw new InvalidOperationException(
                $"{contractLabel} deserialized to null; refusing an empty contract.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{contractLabel} could not be parsed strictly at line {ex.LineNumber}, byte " +
                $"position {ex.BytePositionInLine}: {(ex.Path is null ? "" : $"path '{ex.Path}'; ")}" +
                "the value was withheld. Unknown, misplaced or mistyped members are rejected " +
                "so a typo can never silently select a default.");
        }
    }

    private static JsonSerializerOptions StrictOptions(int maxDepth) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        MaxDepth = maxDepth,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Bounded owning-path rendering, e.g. "sandbox.roles.coder". Depth is bounded by
    /// construction; a final bound keeps any future change honest.</summary>
    private static string CurrentPath(Stack<string> path)
    {
        var joined = string.Join(".", path.Reverse());
        return joined.Length <= 256 ? (joined.Length == 0 ? "<root>" : joined) : joined[^256..];
    }
}
