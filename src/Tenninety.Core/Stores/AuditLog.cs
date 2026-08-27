using System.Text;
using System.Text.Json.Serialization;

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
    private readonly object _lock = new();
    public string Path { get; }
    public AuditLog(string? path = null) => Path = path ?? TenNinety.Resolve(TenNinety.AuditFile);

    public void Append(string @event, string? wp = null, string detail = "")
    {
        var line = Json.SerializeCompact(new AuditEvent { Event = @event, WorkPackageId = wp, Detail = detail });
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
                    catch { return new AuditEvent { Event = "UNPARSEABLE", Detail = l }; }
                })
                .ToList();
        }
    }
}
