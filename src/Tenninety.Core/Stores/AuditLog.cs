using System.Text;
using System.Text.Json.Serialization;
using Tenninety.Core.Security;

namespace Tenninety.Core.Stores;

public sealed class AuditEvent
{
    [JsonPropertyName("ts")]
    public string Timestamp { get; init; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("event")]
    public string Event { get; init; } = "";

    [JsonPropertyName("wp")]
    public string? WorkPackageId { get; init; }

    [JsonPropertyName("detail")]
    public string Detail { get; init; } = "";
}

/// <summary>Append-only JSONL audit trail (.tenninety/audit-log.jsonl). Included in pivot snapshots.</summary>
public sealed class AuditLog
{
    public const int MaxDetailChars = 4000;
    private readonly object _lock = new();
    public string Path { get; }
    public AuditLog(string? path = null) => Path = path ?? TenNinety.Resolve(TenNinety.AuditFile);

    public void Append(string @event, string? wp = null, string detail = "")
    {
        var line = Json.SerializeCompact(new AuditEvent
        {
            Event = Sanitizer.SanitizeDiagnostic(@event ?? "", 128),
            WorkPackageId = wp is null ? null : Sanitizer.SanitizeDiagnostic(wp, 256),
            Detail = Sanitizer.SanitizeDiagnostic(detail ?? "", MaxDetailChars),
        });
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public List<AuditEvent> ReadTail(int count = 50)
    {
        if (!File.Exists(Path)) return new List<AuditEvent>();
        lock (_lock)
        {
            var lines = File.ReadAllLines(Path);
            return lines
                .Skip(Math.Max(0, lines.Length - count))
                .Select(l =>
                {
                    try { return Json.Deserialize<AuditEvent>(l); }
                    catch
                    {
                        return new AuditEvent
                        {
                            Event = "UNPARSEABLE",
                            Detail = Sanitizer.SanitizeDiagnostic(l, MaxDetailChars),
                        };
                    }
                })
                .ToList();
        }
    }
}
