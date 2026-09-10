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
    public const int MaxRecordBytes = 32 * 1024;
    private const int TailBufferBytes = 4096;
    private const byte LineFeed = 0x0A;
    private const byte CarriageReturn = 0x0D;
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
        if (Encoding.UTF8.GetByteCount(line) > MaxRecordBytes)
            throw new InvalidOperationException(
                "the sanitized audit event exceeded the bounded JSONL record size.");
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.AppendAllText(Path, line + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public List<AuditEvent> ReadTail(int count = 50)
    {
        if (count <= 0) return [];
        lock (_lock)
        {
            if (!File.Exists(Path)) return [];
            using var stream = new FileStream(
                Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                TailBufferBytes, FileOptions.RandomAccess);
            if (stream.Length == 0) return [];

            var cursor = stream.Length;
            var pendingEmptyAtStart = false;
            stream.Position = cursor - 1;
            if (stream.ReadByte() == LineFeed)
            {
                cursor--;
                pendingEmptyAtStart = cursor == 0;
            }

            var events = new List<AuditEvent>(Math.Min(count, 128));
            var readBuffer = new byte[TailBufferBytes];
            var reverseRecord = new byte[MaxRecordBytes + 1];
            while (events.Count < count && (cursor > 0 || pendingEmptyAtStart))
            {
                if (pendingEmptyAtStart && cursor == 0)
                {
                    pendingEmptyAtStart = false;
                    events.Add(ParseRecord(""));
                    continue;
                }

                var line = ReadPreviousRecord(
                    stream, ref cursor, readBuffer, reverseRecord,
                    ref pendingEmptyAtStart, out var oversized);
                if (oversized)
                {
                    events.Add(new AuditEvent
                    {
                        Event = "UNPARSEABLE",
                        Detail = $"audit record exceeds the {MaxRecordBytes}-byte limit; " +
                                 "content withheld; older records omitted.",
                    });
                    break;
                }
                events.Add(ParseRecord(line!));
            }

            events.Reverse();
            return events;
        }
    }

    private static string? ReadPreviousRecord(
        FileStream stream,
        ref long cursor,
        byte[] readBuffer,
        byte[] reverseRecord,
        ref bool pendingEmptyAtStart,
        out bool oversized)
    {
        oversized = false;
        var recordLength = 0;
        var startsAtBeginning = false;
        while (cursor > 0)
        {
            var blockStart = Math.Max(0L, cursor - readBuffer.Length);
            var blockLength = checked((int)(cursor - blockStart));
            stream.Position = blockStart;
            stream.ReadExactly(readBuffer.AsSpan(0, blockLength));

            for (var i = blockLength - 1; i >= 0; i--)
            {
                var value = readBuffer[i];
                var absolute = blockStart + i;
                if (value == LineFeed)
                {
                    cursor = absolute;
                    pendingEmptyAtStart = absolute == 0;
                    return DecodeRecord(reverseRecord, recordLength, startsAtBeginning: false);
                }

                if (recordLength == reverseRecord.Length)
                {
                    oversized = true;
                    return null;
                }
                reverseRecord[recordLength++] = value;
                if (recordLength > MaxRecordBytes && reverseRecord[0] != CarriageReturn)
                {
                    oversized = true;
                    return null;
                }
            }
            cursor = blockStart;
        }

        startsAtBeginning = true;
        return DecodeRecord(reverseRecord, recordLength, startsAtBeginning);
    }

    private static string DecodeRecord(
        byte[] reverseRecord, int recordLength, bool startsAtBeginning)
    {
        Array.Reverse(reverseRecord, 0, recordLength);
        if (recordLength > 0 && reverseRecord[recordLength - 1] == CarriageReturn)
            recordLength--;
        var line = Encoding.UTF8.GetString(reverseRecord, 0, recordLength);
        return startsAtBeginning && line.Length > 0 && line[0] == '\uFEFF'
            ? line[1..]
            : line;
    }

    private static AuditEvent ParseRecord(string line)
    {
        try { return Json.Deserialize<AuditEvent>(line); }
        catch
        {
            return new AuditEvent
            {
                Event = "UNPARSEABLE",
                Detail = Sanitizer.SanitizeDiagnostic(line, MaxDetailChars),
            };
        }
    }
}
