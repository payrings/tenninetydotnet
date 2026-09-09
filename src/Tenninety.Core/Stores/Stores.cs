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

    /// <summary>Strict bounded load: plans are UNTRUSTED (they arrive from the Frontier and
    /// are partially rewritten by pivot proposals), so unknown members, duplicate fields and
    /// excessive size/depth fail with a clear validation error instead of silent defaults.
    /// The file is read through a race-conscious bounded stream (at most the size bound plus
    /// one byte is ever read) — never a full <c>ReadAllBytes</c> ahead of the bound.</summary>
    public Plan Load()
    {
        var bytes = StrictJsonIngestion.ReadBounded(
            Path, StrictJsonIngestion.MaxPlanBytes, "plan.json");
        StrictJsonIngestion.EnsureStrictShape(bytes, StrictJsonIngestion.MaxPlanBytes, "plan.json");
        return StrictJsonIngestion.Deserialize<Plan>(bytes, "plan.json");
    }

    public void Save(Plan plan) => File.WriteAllText(Path, Json.Serialize(plan));
}

public sealed class StateStore
{
    private const int MaxStateBytes = 16 * 1024 * 1024;
    public string Path { get; }
    public StateStore(string? path = null) => Path = path ?? TenNinety.Resolve(TenNinety.StateFile);

    /// <summary>Deterministic test seam; production leaves this null.</summary>
    internal Action<RuntimeState>? BeforeSave { get; set; }

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
        BeforeSave?.Invoke(state);
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
            if (attempt.ExecutionId is not null && !PromotionTransactionStore.IsExecutionId(attempt.ExecutionId))
                throw new InvalidOperationException($"state.json has an invalid execution identity for '{wpId}'.");
        }
    }
}

/// <summary>Atomic repository-local journal for a single in-flight squash promotion. The daemon
/// lock serializes owners; the file lock protects readers from observing a replacement in flight.</summary>
public sealed class PromotionTransactionStore
{
    private const int MaxTransactionBytes = 16 * 1024;
    public string Path { get; }

    public PromotionTransactionStore(string? path = null) =>
        Path = path ?? TenNinety.Resolve(TenNinety.PromotionFile);

    public bool Exists() => File.Exists(Path);

    public PromotionTransaction? Load()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (!Directory.Exists(directory)) return null;
        using var fileLock = AcquireFileLock();
        return LoadUnlocked();
    }

    private PromotionTransaction? LoadUnlocked()
    {
        if (!File.Exists(Path)) return null;
        if (new FileInfo(Path).LinkTarget is not null)
            throw new InvalidOperationException(
                $"{TenNinety.PromotionFile} is redirected; recovery evidence is not trusted.");
        var bytes = StrictJsonIngestion.ReadBounded(Path, MaxTransactionBytes,
            TenNinety.PromotionFile);
        StrictJsonIngestion.EnsureStrictShape(bytes, MaxTransactionBytes,
            TenNinety.PromotionFile);
        var transaction = StrictJsonIngestion.Deserialize<PromotionTransaction>(
            bytes, TenNinety.PromotionFile);
        Validate(transaction);
        return transaction;
    }

    public void Save(PromotionTransaction transaction)
    {
        Validate(transaction);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var fileLock = AcquireFileLock();
        if (File.Exists(Path))
            throw new InvalidOperationException(
                "another promotion transaction is already pending; recovery is required first.");
        var tmp = $"{Path}.tmp.{Guid.NewGuid():N}";
        try
        {
            var json = Json.Serialize(transaction);
            if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxTransactionBytes)
                throw new InvalidOperationException(
                    $"{TenNinety.PromotionFile} exceeds its persisted size bound.");
            using (var stream = new FileStream(
                       tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, Path);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public void Complete(PromotionTransaction expected)
    {
        using var fileLock = AcquireFileLock();
        var actual = LoadUnlocked();
        if (actual is null) return;
        if (Json.SerializeCompact(actual) != Json.SerializeCompact(expected))
            throw new InvalidOperationException(
                "the durable promotion transaction changed; refusing to remove recovery evidence.");
        File.Delete(Path);
        if (File.Exists(Path))
            throw new InvalidOperationException(
                "the durable promotion transaction could not be removed after completion.");
    }

    private FileStream AcquireFileLock()
    {
        var lockPath = Path + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    internal static bool IsExecutionId(string value) =>
        value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static void Validate(PromotionTransaction transaction)
    {
        var workPackage = transaction.Kind == TenNinety.PromotionKinds.WorkPackage;
        var hotfix = transaction.Kind == TenNinety.PromotionKinds.Hotfix;
        if ((!workPackage && !hotfix) || !IsExecutionId(transaction.ExecutionId) ||
            string.IsNullOrWhiteSpace(transaction.Branch) || transaction.Branch.Length > 256 ||
            transaction.Branch.Any(char.IsControl) || !IsSha(transaction.ExpectedBaseSha) ||
            !IsSha(transaction.CandidateSha) || !IsSha(transaction.PromotionSha) ||
            transaction.ExpectedBaseSha.Equals(transaction.PromotionSha,
                StringComparison.OrdinalIgnoreCase) ||
            (workPackage && (string.IsNullOrWhiteSpace(transaction.WorkPackageId) ||
                             transaction.WorkPackageId.Length > 128 ||
                             transaction.WorkPackageId.Any(char.IsControl) ||
                             transaction.Branch != TenNinety.WorkBranchPrefix +
                                                   transaction.WorkPackageId)) ||
            (hotfix && (transaction.WorkPackageId is not null ||
                        !transaction.Branch.StartsWith(TenNinety.HotfixBranchPrefix,
                            StringComparison.Ordinal))))
            throw new InvalidOperationException(
                $"{TenNinety.PromotionFile} contains invalid or incomplete recovery evidence.");
    }
}

public sealed class ConfigStore
{
    public string Path { get; }
    public ConfigStore(string? path = null) => Path = path ?? TenNinety.Resolve(TenNinety.ConfigFile);

    public bool Exists() => File.Exists(Path);

    /// <summary>Strict bounded load for the operator-edited configuration: unknown members
    /// (e.g. a "provider_mod" typo), duplicate fields at any depth, explicit nulls and
    /// excessive size/depth are rejected with a clear error naming the owning field — a typo
    /// must never silently select a default such as mock mode. Omitted fields legitimately
    /// keep their defaults (backward compatible with older valid configs). The file is read
    /// through a race-conscious bounded stream (at most the size bound plus one byte is ever
    /// read) — never a full <c>ReadAllBytes</c> ahead of the bound. The shared
    /// <see cref="Json.Options"/> and state.json migration are deliberately untouched.</summary>
    public TenNinetyConfig Load()
    {
        if (!File.Exists(Path)) return DefaultValidated();
        var bytes = StrictJsonIngestion.ReadBounded(
            Path, StrictJsonIngestion.MaxConfigBytes, "config.json");
        StrictJsonIngestion.EnsureStrictShape(bytes, StrictJsonIngestion.MaxConfigBytes, "config.json");
        var config = StrictJsonIngestion.Deserialize<TenNinetyConfig>(bytes, "config.json");
        config.Validate();
        return config;
    }

    private static TenNinetyConfig DefaultValidated()
    {
        var config = new TenNinetyConfig();
        config.Validate();
        return config;
    }

    public void Save(TenNinetyConfig config)
    {
        config.Validate();
        File.WriteAllText(Path, Json.Serialize(config));
    }
}
