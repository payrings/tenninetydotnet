using System.Text.Json;
using System.Text.Json.Serialization;
using Tenninety.Core;

namespace Tenninety.Execution.Sandbox;

/// <summary>Trusted crash journal for disposable attempts. Paths are never inferred by scanning:
/// a gate records exact ownership before Docker creation, updates the exact returned container
/// identity, and removes the record only after proven container and workspace cleanup.</summary>
internal sealed class SandboxResourceJournal
{
    private const int CurrentVersion = 1;
    private const int MaxJournalBytes = 4 * 1024 * 1024;
    private readonly string _repository;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal SandboxResourceJournal(string repositoryPath)
    {
        var full = Path.GetFullPath(repositoryPath);
        _repository = full == "/" ? full : full.TrimEnd('/');
        _path = Path.Combine(_repository, TenNinety.StateDir, "sandbox-resources.json");
        _lockPath = _path + ".lock";
    }

    internal sealed class ResourceRecord
    {
        public string Id { get; set; } = "";
        public string ManagedRoot { get; set; } = "";
        public string AttemptRoot { get; set; } = "";
        public bool OwnedManagedRoot { get; set; }
        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.Ordinal);
        public string? ContainerId { get; set; }
        public string UpdatedUtc { get; set; } = "";
        public string? LastFailure { get; set; }
    }

    private sealed class JournalDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public List<ResourceRecord> Resources { get; set; } = [];
    }

    internal string Track(
        string managedRoot,
        string attemptRoot,
        bool ownedManagedRoot,
        IReadOnlyDictionary<string, string> labels)
    {
        var record = new ResourceRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            ManagedRoot = managedRoot,
            AttemptRoot = attemptRoot,
            OwnedManagedRoot = ownedManagedRoot,
            Labels = new Dictionary<string, string>(labels, StringComparer.Ordinal),
            UpdatedUtc = DateTimeOffset.UtcNow.ToString("O"),
        };
        Validate(record, requirePathsPresent: true);
        Update(document => document.Resources.Add(record));
        return record.Id;
    }

    internal void SetContainer(string resourceId, string containerId)
    {
        DockerValidation.RequireContainerId(containerId, "sandbox journal container id");
        Update(document =>
        {
            var record = RequireRecord(document, resourceId);
            record.ContainerId = containerId;
            record.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
        });
    }

    internal void ClearContainer(string resourceId) => Update(document =>
    {
        var record = RequireRecord(document, resourceId);
        record.ContainerId = null;
        record.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
    });

    internal void Complete(string resourceId) => Update(document =>
    {
        var removed = document.Resources.RemoveAll(record => record.Id == resourceId);
        if (removed != 1)
            throw new InvalidOperationException(
                "sandbox resource journal completion did not match exactly one owned record.");
    });

    internal void RecordFailure(string resourceId, string category) => Update(document =>
    {
        var record = RequireRecord(document, resourceId);
        record.LastFailure = Bound(category);
        record.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
    });

    internal IReadOnlyList<ResourceRecord> ReadAll()
    {
        using var fileLock = AcquireLock();
        return ReadUnlocked().Resources.Select(Clone).ToList().AsReadOnly();
    }

    private void Update(Action<JournalDocument> update)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        ValidateStorageEntries();
        using var fileLock = AcquireLock();
        var document = ReadUnlocked();
        update(document);
        if (document.Resources.Count > 10_000)
            throw new InvalidOperationException("sandbox resource journal record bound exceeded.");
        foreach (var record in document.Resources) Validate(record, requirePathsPresent: false);
        var tmp = _path + ".tmp." + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, JsonSerializer.Serialize(document, _json));
        SetOwnerOnly(tmp);
        File.Move(tmp, _path, overwrite: true);
    }

    private JournalDocument ReadUnlocked()
    {
        ValidateStorageEntries();
        if (!File.Exists(_path)) return new JournalDocument();
        SetOwnerOnly(_path);
        var info = new FileInfo(_path);
        if (info.Length is < 2 or > MaxJournalBytes)
            throw new InvalidOperationException("sandbox resource journal size is invalid.");
        var bytes = File.ReadAllBytes(_path);
        StrictJson.EnsureNoDuplicateFields(bytes);
        var document = JsonSerializer.Deserialize<JournalDocument>(bytes, _json)
            ?? throw new InvalidOperationException("sandbox resource journal is empty.");
        if (document.Version != CurrentVersion || document.Resources is null)
            throw new InvalidOperationException("sandbox resource journal version is unsupported.");
        if (document.Resources.Select(record => record.Id).Distinct(StringComparer.Ordinal).Count() !=
            document.Resources.Count)
            throw new InvalidOperationException("sandbox resource journal contains duplicate records.");
        foreach (var record in document.Resources) Validate(record, requirePathsPresent: false);
        return document;
    }

    private void Validate(ResourceRecord record, bool requirePathsPresent)
    {
        if (record.Id.Length != 32 || !record.Id.All(Uri.IsHexDigit) ||
            !DateTimeOffset.TryParseExact(record.UpdatedUtc, "O", null,
                System.Globalization.DateTimeStyles.RoundtripKind, out _) ||
            record.Labels is null || record.LastFailure?.Length > 512)
            throw new InvalidOperationException("sandbox resource journal record shape is invalid.");
        _ = DockerContainerScope.FromManagementIdentity(record.Labels);
        if (!string.Equals(record.Labels["tenninety.instance"], "tenninety",
                StringComparison.Ordinal) ||
            !string.Equals(record.Labels["tenninety.repository"],
                SandboxPolicy.RepositoryIdentity(_repository), StringComparison.Ordinal))
            throw new InvalidOperationException(
                "sandbox resource journal management scope does not match this repository.");
        if (record.ContainerId is not null)
            DockerValidation.RequireContainerId(record.ContainerId, "sandbox journal container id");

        var managed = TrustedPathValidation.ValidateAbsoluteShape(
            record.ManagedRoot, "journal managed root");
        var attempt = TrustedPathValidation.ValidateAbsoluteShape(
            record.AttemptRoot, "journal attempt root");
        if (!attempt.StartsWith(managed + "/", StringComparison.Ordinal) ||
            attempt.Split('/').Length - managed.Split('/').Length != 1 ||
            !Path.GetFileName(attempt).StartsWith("attempt-", StringComparison.Ordinal) ||
            managed == _repository || managed.StartsWith(_repository + "/", StringComparison.Ordinal) ||
            _repository.StartsWith(managed + "/", StringComparison.Ordinal))
            throw new InvalidOperationException("sandbox resource journal path binding is invalid.");
        if (requirePathsPresent)
        {
            TrustedPathValidation.EnsureRealDirectoryChain(managed, "journal managed root");
            if (TrustedWorkspaceDeletion.InspectEntryNoFollow(attempt) !=
                TrustedWorkspaceDeletion.ManagedEntryKind.RealDirectory)
                throw new InvalidOperationException(
                    "sandbox resource journal can track only an existing real attempt directory.");
        }
    }

    private FileStream AcquireLock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        ValidateStorageEntries();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                SetOwnerOnly(_lockPath);
                return stream;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private void ValidateStorageEntries()
    {
        var directory = Path.GetDirectoryName(_path)!;
        TrustedPathValidation.EnsureRealDirectoryChain(
            directory, "sandbox resource journal directory");
        foreach (var file in new[] { _path, _lockPath })
        {
            var kind = TrustedWorkspaceDeletion.InspectEntryNoFollow(file);
            if (kind is not (TrustedWorkspaceDeletion.ManagedEntryKind.Absent or
                TrustedWorkspaceDeletion.ManagedEntryKind.RealFile))
                throw new InvalidOperationException(
                    "sandbox resource journal storage contains an unexpected redirect or directory.");
        }
    }

    private static ResourceRecord RequireRecord(JournalDocument document, string resourceId) =>
        document.Resources.SingleOrDefault(record => record.Id == resourceId)
        ?? throw new InvalidOperationException("sandbox resource journal record is missing.");

    private static ResourceRecord Clone(ResourceRecord record) => new()
    {
        Id = record.Id,
        ManagedRoot = record.ManagedRoot,
        AttemptRoot = record.AttemptRoot,
        OwnedManagedRoot = record.OwnedManagedRoot,
        Labels = new Dictionary<string, string>(record.Labels, StringComparer.Ordinal),
        ContainerId = record.ContainerId,
        UpdatedUtc = record.UpdatedUtc,
        LastFailure = record.LastFailure,
    };

    private static string Bound(string value) =>
        value.Length <= 512 ? value : value[..500] + "...[bounded]";

    private static void SetOwnerOnly(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>Per-gate ownership capability over one journal record.</summary>
internal sealed class SandboxAttemptOwnership
{
    private readonly SandboxResourceJournal _journal;
    private readonly string _managedRoot;
    private readonly bool _ownedManagedRoot;
    private readonly IReadOnlyDictionary<string, string> _labels;
    private string? _resourceId;
    private string? _attemptRoot;

    internal SandboxAttemptOwnership(
        string repository,
        string managedRoot,
        bool ownedManagedRoot,
        IReadOnlyDictionary<string, string> labels)
    {
        _journal = new SandboxResourceJournal(repository);
        _managedRoot = managedRoot;
        _ownedManagedRoot = ownedManagedRoot;
        _labels = labels;
    }

    internal void RecordAttempt(string attemptRoot)
    {
        if (_resourceId is not null)
            throw new InvalidOperationException("sandbox attempt ownership was already recorded.");
        _attemptRoot = attemptRoot;
        _resourceId = _journal.Track(
            _managedRoot, attemptRoot, _ownedManagedRoot, _labels);
    }

    internal void SetContainer(string containerId)
    {
        if (_resourceId is { } id) _journal.SetContainer(id, containerId);
    }

    internal void ContainerRemoved()
    {
        if (_resourceId is { } id) _journal.ClearContainer(id);
    }

    internal void CompleteAfterWorkspaceDeletion()
    {
        if (_resourceId is not { } id) return;
        _journal.Complete(id);
        _resourceId = null;
    }

    internal void CompleteIfAttemptAbsent()
    {
        if (_resourceId is not { } id || _attemptRoot is null) return;
        if (TrustedWorkspaceDeletion.InspectEntryNoFollow(_attemptRoot) !=
            TrustedWorkspaceDeletion.ManagedEntryKind.Absent)
            throw new InvalidOperationException(
                "a journaled partial sandbox attempt remains and is quarantined.");
        _journal.Complete(id);
        _resourceId = null;
    }
}
