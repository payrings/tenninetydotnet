using System.Text.Json;
using System.Text.Json.Serialization;
using Tenninety.Core.Models;

namespace Tenninety.Core.Stores;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Compact single-line form used for JSONL files (audit log).</summary>
    public static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static string SerializeCompact<T>(T value) => JsonSerializer.Serialize(value, Compact);

    public static T Deserialize<T>(string text)
    {
        var result = JsonSerializer.Deserialize<T>(text, Options);
        return result ?? throw new InvalidOperationException($"Failed to deserialize {typeof(T).Name}.");
    }
}

/// <summary>Reads/writes the JSON contracts under .tenninety/. Git-first: these files are committed by init.</summary>
public sealed class PlanStore
{
    public string Path { get; }
    public PlanStore(string? path = null) => Path = path ?? TenNinety.Resolve(TenNinety.PlanFile);

    public bool Exists() => File.Exists(Path);
    public Plan Load() => Json.Deserialize<Plan>(File.ReadAllText(Path));
    public void Save(Plan plan) => File.WriteAllText(Path, Json.Serialize(plan));
}

public sealed class StateStore
{
    private const int MaxStateBytes = 16 * 1024 * 1024;
    public string Path { get; }
    public StateStore(string? path = null) => Path = path ?? TenNinety.Resolve(TenNinety.StateFile);

    public bool Exists() => File.Exists(Path);
    public RuntimeState Load()
    {
        var state = LoadFileOrDefault();
        Validate(state);
        return state;
    }
    public void Save(RuntimeState state)
    {
        Validate(state);
        lock (this)
        {
            // Cross-process coordination: an exclusive lock file serialises writers from
            // different processes; the payload lands via a UNIQUE temp name + atomic move,
            // so a crash can never leave a truncated or half-written state.json.
            using var fileLock = AcquireFileLock();
            SaveUnlocked(state);
        }
    }

    public RuntimeState Update(Action<RuntimeState> update)
    {
        lock (this)
        {
            using var fileLock = AcquireFileLock();
            var state = LoadFileOrDefault();
            Validate(state);
            update(state);
            Validate(state);
            SaveUnlocked(state);
            return state;
        }
    }

    private FileStream AcquireFileLock()
    {
        var lockPath = Path + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private void SaveUnlocked(RuntimeState state)
    {
        var tmp = $"{Path}.tmp.{Guid.NewGuid():N}";
        var json = Json.Serialize(state);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxStateBytes)
            throw new InvalidOperationException("state.json exceeds its persisted size bound.");
        File.WriteAllText(tmp, json);
        File.Move(tmp, Path, overwrite: true);
    }

    private RuntimeState LoadFileOrDefault()
    {
        if (!File.Exists(Path)) return new RuntimeState();
        if (new FileInfo(Path).Length > MaxStateBytes)
            throw new InvalidOperationException("state.json exceeds its persisted size bound.");
        var bytes = File.ReadAllBytes(Path);
        EnsureNoDuplicateFields(bytes);
        return Json.Deserialize<RuntimeState>(System.Text.Encoding.UTF8.GetString(bytes));
    }

    private static void EnsureNoDuplicateFields(byte[] json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64,
        });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                objects.Push(new HashSet<string>(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject)
                objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString() ?? "";
                if (name.Length > 256 || !objects.Peek().Add(name))
                    throw new InvalidOperationException(
                        "state.json contains an overlong or duplicate field.");
            }
        }
    }

    private static void Validate(RuntimeState state)
    {
        if (state.Attempts is null || state.QueueStatus is null ||
            state.SandboxRecovery is null || state.SandboxRecovery.Quarantined is null)
            throw new InvalidOperationException("state.json contains a null collection.");
        var recovery = state.SandboxRecovery;
        var allowedRecoveryStatuses = new HashSet<string>(StringComparer.Ordinal)
            { "not-run", "not-required", "clean", "recovered", "quarantined" };
        if (recovery.ContainersFound < 0 || recovery.ContainersRemoved < 0 ||
            recovery.WorkspacesFound < 0 || recovery.WorkspacesRemoved < 0 ||
            recovery.ContainersRemoved > recovery.ContainersFound ||
            recovery.WorkspacesRemoved > recovery.WorkspacesFound ||
            recovery.Quarantined.Count > 1000 || recovery.Detail is null ||
            recovery.Detail.Length > 2000 || recovery.Detail.Any(char.IsControl) ||
            !allowedRecoveryStatuses.Contains(recovery.Status ?? "") ||
            recovery.Quarantined.Any(value => string.IsNullOrWhiteSpace(value) ||
                value.Length > 128 || value.Any(char.IsControl)) ||
            recovery.Quarantined.Distinct(StringComparer.Ordinal).Count() !=
                recovery.Quarantined.Count ||
            (recovery.Status == "quarantined") != (recovery.Quarantined.Count > 0) ||
            (recovery.Status == "not-run"
                ? recovery.LastRunUtc is not null
                : !DateTimeOffset.TryParseExact(
                    recovery.LastRunUtc, "O", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var recoveryTime) ||
                  recoveryTime.Offset != TimeSpan.Zero))
            throw new InvalidOperationException("state.json contains invalid sandbox recovery facts.");
        foreach (var (wpId, attempt) in state.Attempts)
        {
            if (attempt is null || attempt.Feedback is null || attempt.Advice is null ||
                attempt.LastFailureReasons is null)
                throw new InvalidOperationException($"state.json has incomplete attempt data for '{wpId}'.");
            if (attempt.Count < 0 || attempt.Total < 0 || attempt.Max < 1 ||
                attempt.Count == int.MaxValue || attempt.Total == int.MaxValue)
                throw new InvalidOperationException($"state.json has invalid attempt counters for '{wpId}'.");
        }
    }
}

public sealed class ConfigStore
{
    public string Path { get; }
    public ConfigStore(string? path = null) => Path = path ?? TenNinety.Resolve(TenNinety.ConfigFile);

    public bool Exists() => File.Exists(Path);
    public TenNinetyConfig Load()
    {
        var config = File.Exists(Path)
            ? Json.Deserialize<TenNinetyConfig>(File.ReadAllText(Path))
            : new TenNinetyConfig();
        config.Validate();
        return config;
    }

    public void Save(TenNinetyConfig config)
    {
        config.Validate();
        File.WriteAllText(Path, Json.Serialize(config));
    }
}
