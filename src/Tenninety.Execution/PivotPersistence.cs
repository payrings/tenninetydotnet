using System.Security.Cryptography;
using System.Text;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Core.Validation;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Execution;

internal enum PivotPersistencePhase
{
    IntentRecorded,
    PlanWritten,
    StateWritten,
    BeforeCommit,
    AfterCommit,
}

/// <summary>Persists an approved pivot as a recoverable plan/state pair. The next pair is
/// staged in ignored payload files before the intent appears; the intent remains until both
/// live files and the exact plan-only Git commit are proven complete.</summary>
public sealed class PivotPersistence
{
    internal const string CommitMessage = "pivot applied";

    private readonly IGitService _git;
    private readonly PlanStore _plans;
    private readonly StateStore _states;
    private readonly PivotTransactionStore _transactions;
    private readonly string _nextPlanPath;
    private readonly string _nextStatePath;

    public PivotPersistence(IGitService git, PlanStore plans, StateStore states)
    {
        _git = git;
        _plans = plans;
        _states = states;
        var stateDirectory = Path.Combine(git.RepoPath, TenNinety.StateDir);
        _transactions = new PivotTransactionStore(
            Path.Combine(stateDirectory, TenNinety.PivotFile));
        _nextPlanPath = Path.Combine(stateDirectory, TenNinety.PivotPlanFile);
        _nextStatePath = Path.Combine(stateDirectory, TenNinety.PivotStateFile);
    }

    /// <summary>Deterministic process-interruption seam; production leaves this null.</summary>
    internal Action<PivotPersistencePhase>? Failpoint { get; set; }

    public bool HasPending => _transactions.Exists();

    public PivotService.ApplyResult ApplyApproved(
        PivotProposal proposal, Plan plan, RuntimeState state, DaemonLockLease daemonLock)
    {
        using var operation = daemonLock.BeginUseFor(_git.RepoPath);
        operation.EnsureLiveFor(_git.RepoPath);

        if (_transactions.Exists())
            throw RecoveryRequired(
                "another approved pivot already has pending recovery evidence.");
        if (RuntimeGitignoreMigration.CountPivotJournalsInOtherWorktrees(_git) > 0)
            throw RecoveryRequired(
                "a linked worktree owns pending pivot recovery evidence.");
        RequireArtifactsIgnored();
        if (_git.CurrentBranch() != TenNinety.MainBranch || !_git.IsClean())
            throw new InvalidOperationException(
                "pivot persistence requires a clean workspace on main.");
        if (!_plans.Exists() || !_states.Exists())
            throw new InvalidOperationException(
                "pivot persistence requires an existing plan.json and state.json pair.");

        CleanupPayloads();
        CleanupPlanTemps();
        RequireSourceMatchesPersistedPair(plan, state);
        var candidatePlan = Json.Deserialize<Plan>(Json.Serialize(plan));
        var candidateState = Json.Deserialize<RuntimeState>(Json.Serialize(state));
        var result = PivotService.Apply(proposal, candidatePlan, candidateState);
        EnsureMatchingPair(candidatePlan, candidateState);

        try
        {
            new PlanStore(_nextPlanPath).Save(candidatePlan);
            new StateStore(_nextStatePath).Save(candidateState);
            var transaction = new PivotTransaction
            {
                TransactionId = Guid.NewGuid().ToString("N"),
                ExpectedMainSha = _git.HeadSha(),
                OldPlanSha256 = HashFile(
                    _plans.Path, StrictJsonIngestion.MaxPlanBytes, "plan.json"),
                OldStateSha256 = HashFile(
                    _states.Path, StateStore.MaxStateBytes, "state.json"),
                NewPlanSha256 = HashFile(
                    _nextPlanPath, StrictJsonIngestion.MaxPlanBytes, TenNinety.PivotPlanFile),
                NewStateSha256 = HashFile(
                    _nextStatePath, StateStore.MaxStateBytes, TenNinety.PivotStateFile),
            };
            _transactions.Save(transaction);
            Failpoint?.Invoke(PivotPersistencePhase.IntentRecorded);

            _plans.Save(candidatePlan);
            Failpoint?.Invoke(PivotPersistencePhase.PlanWritten);
            _states.Save(candidateState);
            CopyPlan(candidatePlan, plan);
            CopyState(candidateState, state);
            Failpoint?.Invoke(PivotPersistencePhase.StateWritten);

            CompleteCommit(transaction);
            _transactions.Complete(transaction);
            CleanupPayloads();
            return result;
        }
        catch (Exception ex)
        {
            if (!_transactions.Exists())
            {
                CleanupPayloads();
                throw;
            }
            throw RecoveryRequired(
                $"pivot persistence was interrupted ({ex.GetType().Name}); the next " +
                "plan/state pair was not discarded.", ex);
        }
    }

    /// <summary>Completes a pending local pivot or refuses with its evidence intact. Returns
    /// true when a transaction was recovered, false when no local transaction existed.</summary>
    public bool RecoverPending(DaemonLockLease daemonLock)
    {
        using var operation = daemonLock.BeginUseFor(_git.RepoPath);
        operation.EnsureLiveFor(_git.RepoPath);

        var linked = RuntimeGitignoreMigration.CountPivotJournalsInOtherWorktrees(_git);
        if (linked > 0)
            throw RecoveryRequired(
                $"{linked} linked worktree pivot transaction(s) require recovery from " +
                "their owning worktree.");

        PivotTransaction? transaction = null;
        try
        {
            transaction = _transactions.Load();
            if (transaction is null)
            {
                CleanupPayloads();
                CleanupPlanTemps();
                return false;
            }

            RequireArtifactsIgnored();
            CleanupPlanTemps();
            if (_git.CurrentBranch() != TenNinety.MainBranch)
                throw new InvalidOperationException(
                    $"pivot recovery requires branch '{TenNinety.MainBranch}'.");

            var candidatePlan = new PlanStore(_nextPlanPath).Load();
            var candidateState = new StateStore(_nextStatePath).Load();
            var validation = PlanValidator.Validate(candidatePlan);
            if (!validation.IsValid)
                throw new InvalidOperationException(
                    "the staged pivot plan is invalid: " + string.Join("; ", validation.Errors));
            EnsureMatchingPair(candidatePlan, candidateState);
            RequireHash(_nextPlanPath, StrictJsonIngestion.MaxPlanBytes,
                TenNinety.PivotPlanFile, transaction.NewPlanSha256);
            RequireHash(_nextStatePath, StateStore.MaxStateBytes,
                TenNinety.PivotStateFile, transaction.NewStateSha256);

            var planHash = HashFile(
                _plans.Path, StrictJsonIngestion.MaxPlanBytes, "plan.json");
            var stateHash = HashFile(
                _states.Path, StateStore.MaxStateBytes, "state.json");
            RequireOldOrNew("plan.json", planHash,
                transaction.OldPlanSha256, transaction.NewPlanSha256);
            RequireOldOrNew("state.json", stateHash,
                transaction.OldStateSha256, transaction.NewStateSha256);

            var head = _git.FindCommit(TenNinety.MainBranch)?.Sha
                ?? throw new InvalidOperationException("main has no commit for pivot recovery.");
            if (head == transaction.ExpectedMainSha)
            {
                RequireNoUnrelatedChanges(transaction, planHash);
                if (planHash != transaction.NewPlanSha256)
                    _plans.Save(candidatePlan);
                if (stateHash != transaction.NewStateSha256)
                    _states.Save(candidateState);
                RequireHash(_plans.Path, StrictJsonIngestion.MaxPlanBytes,
                    "plan.json", transaction.NewPlanSha256);
                RequireHash(_states.Path, StateStore.MaxStateBytes,
                    "state.json", transaction.NewStateSha256);
                CompleteCommit(transaction);
            }
            else
            {
                VerifyCompletedCommit(transaction, head, planHash, stateHash);
            }

            _transactions.Complete(transaction);
            CleanupPayloads();
            return true;
        }
        catch (Exception ex)
        {
            if (transaction is null && !_transactions.Exists()) throw;
            throw RecoveryRequired(
                $"the pending pivot could not be reconciled ({ex.GetType().Name}); " +
                "the journal and staged pair were preserved.", ex);
        }
    }

    private void CompleteCommit(PivotTransaction transaction)
    {
        var planChanged = transaction.OldPlanSha256 != transaction.NewPlanSha256;
        var head = _git.FindCommit(TenNinety.MainBranch)?.Sha;
        if (head != transaction.ExpectedMainSha)
            throw new InvalidOperationException(
                "main changed while the approved pivot was being persisted.");
        RequireHash(_plans.Path, StrictJsonIngestion.MaxPlanBytes,
            "plan.json", transaction.NewPlanSha256);
        RequireHash(_states.Path, StateStore.MaxStateBytes,
            "state.json", transaction.NewStateSha256);

        Failpoint?.Invoke(PivotPersistencePhase.BeforeCommit);
        if (_git.CurrentBranch() != TenNinety.MainBranch ||
            _git.HeadSha() != transaction.ExpectedMainSha ||
            _git.FindCommit(TenNinety.MainBranch)?.Sha != transaction.ExpectedMainSha)
            throw new InvalidOperationException(
                "the checkout or main changed before the pivot commit.");
        RequireHash(_plans.Path, StrictJsonIngestion.MaxPlanBytes,
            "plan.json", transaction.NewPlanSha256);
        RequireHash(_states.Path, StateStore.MaxStateBytes,
            "state.json", transaction.NewStateSha256);
        if (planChanged)
        {
            if (!_git.IsCleanExcept(PivotPlanRelativePath()))
                throw new InvalidOperationException(
                    "unrelated workspace edits appeared during pivot persistence; they were preserved.");
            var commit = _git.CommitPaths([PivotPlanRelativePath()], CommitMessage)
                ?? throw new InvalidOperationException(
                    "the changed pivot plan did not produce the expected Git commit.");
            if (_git.ResolveCommitParent(commit) != transaction.ExpectedMainSha ||
                _git.CurrentBranch() != TenNinety.MainBranch ||
                _git.HeadSha() != commit ||
                _git.FindCommit(TenNinety.MainBranch)?.Sha != commit ||
                !_git.CommitChangesOnlyPath(commit, PivotPlanRelativePath()) ||
                !_git.IsPathClean(PivotPlanRelativePath()))
                throw new InvalidOperationException(
                    "the pivot commit does not have the expected parent, branch, or path set.");
        }
        else if (!_git.IsClean())
        {
            throw new InvalidOperationException(
                "unrelated workspace edits appeared during pivot persistence; they were preserved.");
        }

        Failpoint?.Invoke(PivotPersistencePhase.AfterCommit);
        RequireHash(_plans.Path, StrictJsonIngestion.MaxPlanBytes,
            "plan.json", transaction.NewPlanSha256);
        RequireHash(_states.Path, StateStore.MaxStateBytes,
            "state.json", transaction.NewStateSha256);
        if (!_git.IsClean())
            throw new InvalidOperationException(
                "workspace edits appeared while completing the pivot; recovery evidence was retained.");
    }

    private void VerifyCompletedCommit(
        PivotTransaction transaction, string head, string planHash, string stateHash)
    {
        if (transaction.OldPlanSha256 == transaction.NewPlanSha256 ||
            planHash != transaction.NewPlanSha256 ||
            stateHash != transaction.NewStateSha256 ||
            !_git.IsPathClean(PivotPlanRelativePath()) ||
            !_git.IsClean())
            throw new InvalidOperationException(
                "main or the live pivot pair differs from the recorded recovery outcome.");
        var commit = _git.FindCommit(head)
            ?? throw new InvalidOperationException("the pivot commit cannot be resolved.");
        if (commit.Subject != CommitMessage ||
            _git.ResolveCommitParent(head) != transaction.ExpectedMainSha ||
            !_git.CommitChangesOnlyPath(head, PivotPlanRelativePath()))
            throw new InvalidOperationException(
                "main advanced to a commit that is not the recorded pivot outcome; history was left untouched.");
    }

    private void RequireNoUnrelatedChanges(PivotTransaction transaction, string planHash)
    {
        var planChanged = transaction.OldPlanSha256 != transaction.NewPlanSha256;
        var planAlreadyWritten = planHash == transaction.NewPlanSha256 && planChanged;
        var clean = planAlreadyWritten
            ? _git.IsCleanExcept(PivotPlanRelativePath())
            : _git.IsClean();
        if (!clean)
            throw new InvalidOperationException(
                "unrelated workspace edits are present; they were not overwritten or committed.");
    }

    private void RequireArtifactsIgnored()
    {
        if (!RuntimeGitignoreMigration.PivotArtifactsAreIgnored(_git))
            throw new InvalidOperationException(
                "pivot recovery files are not effectively ignored; run the runtime-ignore " +
                "migration and remove overriding ignore negations before applying or recovering a pivot.");
    }

    private void RequireSourceMatchesPersistedPair(Plan plan, RuntimeState state)
    {
        var persistedState = _states.Load();
        if (Json.SerializeCompact(persistedState) != Json.SerializeCompact(state))
            throw new InvalidOperationException(
                "runtime progress changed after the pivot snapshot; request fresh pivot advice.");

        var persistedPlan = _plans.Load();
        foreach (var wp in persistedPlan.WorkPackages)
        {
            wp.Status = persistedState.QueueStatus.TryGetValue(wp.Id, out var status) &&
                        status is TenNinety.WpStatus.Done or TenNinety.WpStatus.Blocked or
                            TenNinety.WpStatus.Cancelled
                ? status
                : TenNinety.WpStatus.Pending;
        }
        if (Json.SerializeCompact(persistedPlan) != Json.SerializeCompact(plan))
            throw new InvalidOperationException(
                "the execution plan changed after the pivot snapshot; request fresh pivot advice.");
    }

    private static void EnsureMatchingPair(Plan plan, RuntimeState state)
    {
        foreach (var wp in plan.WorkPackages)
        {
            if (!state.QueueStatus.TryGetValue(wp.Id, out var status) || status != wp.Status)
                throw new InvalidOperationException(
                    $"pivot plan/state status mismatch for '{wp.Id}'.");
        }
    }

    private static void RequireOldOrNew(
        string label, string actual, string oldHash, string newHash)
    {
        if (actual != oldHash && actual != newHash)
            throw new InvalidOperationException(
                $"{label} matches neither side of the pending pivot; unrelated content was preserved.");
    }

    private static void RequireHash(
        string path, long maxBytes, string label, string expected)
    {
        if (HashFile(path, maxBytes, label) != expected)
            throw new InvalidOperationException(
                $"{label} does not match the pending pivot recovery evidence.");
    }

    private static string HashFile(string path, long maxBytes, string label)
    {
        var bytes = StrictJsonIngestion.ReadBounded(path, maxBytes, label);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static void CopyPlan(Plan source, Plan target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.ProjectName = source.ProjectName;
        target.GlobalContext = source.GlobalContext;
        target.ArchitectureMap = source.ArchitectureMap;
        target.WorkPackages = source.WorkPackages;
    }

    private static void CopyState(RuntimeState source, RuntimeState target)
    {
        target.CurrentWp = source.CurrentWp;
        target.ExecutionMode = source.ExecutionMode;
        target.Attempts = source.Attempts;
        target.QueueStatus = source.QueueStatus;
        target.Paused = source.Paused;
        target.StopRequested = source.StopRequested;
        target.SpecHash = source.SpecHash;
        target.SandboxRecovery = source.SandboxRecovery;
    }

    private string PivotPlanRelativePath() =>
        $"{TenNinety.StateDir}/{TenNinety.PlanFile}";

    private void CleanupPayloads()
    {
        TryDelete(_nextPlanPath);
        TryDelete(_nextStatePath);
        TryDelete(_nextStatePath + ".lock");
    }

    private void CleanupPlanTemps()
    {
        var directory = System.IO.Path.GetDirectoryName(_plans.Path)!;
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(
                     directory, System.IO.Path.GetFileName(_plans.Path) + ".tmp.*"))
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static InvalidOperationException RecoveryRequired(
        string detail, Exception? inner = null) =>
        new(detail + $" Execution is refused while '{TenNinety.PivotFile}' exists; " +
            "run 'tenninety start' from its owning worktree to retry recovery.", inner);
}

internal sealed class PivotTransaction
{
    public string TransactionId { get; init; } = "";
    public string ExpectedMainSha { get; init; } = "";
    public string OldPlanSha256 { get; init; } = "";
    public string OldStateSha256 { get; init; } = "";
    public string NewPlanSha256 { get; init; } = "";
    public string NewStateSha256 { get; init; } = "";
}

internal sealed class PivotTransactionStore
{
    private const int MaxTransactionBytes = 16 * 1024;
    public string Path { get; }

    public PivotTransactionStore(string path) => Path = path;

    public bool Exists() => File.Exists(Path);

    public PivotTransaction? Load()
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (!Directory.Exists(directory)) return null;
        using var fileLock = AcquireFileLock();
        return LoadUnlocked();
    }

    public void Save(PivotTransaction transaction)
    {
        Validate(transaction);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var fileLock = AcquireFileLock();
        if (File.Exists(Path))
            throw new InvalidOperationException(
                "another pivot transaction is already pending.");
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(transaction));
        if (bytes.Length > MaxTransactionBytes)
            throw new InvalidOperationException(
                $"{TenNinety.PivotFile} exceeds its persisted size bound.");
        var tmp = $"{Path}.tmp.{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(
                       tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, Path);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    public void Complete(PivotTransaction expected)
    {
        using var fileLock = AcquireFileLock();
        var actual = LoadUnlocked();
        if (actual is null) return;
        if (Json.SerializeCompact(actual) != Json.SerializeCompact(expected))
            throw new InvalidOperationException(
                "the pivot recovery evidence changed; refusing to remove it.");
        File.Delete(Path);
        if (File.Exists(Path))
            throw new InvalidOperationException(
                "the pivot recovery evidence could not be removed after completion.");
    }

    private PivotTransaction? LoadUnlocked()
    {
        if (!File.Exists(Path)) return null;
        if (new FileInfo(Path).LinkTarget is not null)
            throw new InvalidOperationException(
                $"{TenNinety.PivotFile} is redirected; recovery evidence is not trusted.");
        var bytes = StrictJsonIngestion.ReadBounded(
            Path, MaxTransactionBytes, TenNinety.PivotFile);
        StrictJsonIngestion.EnsureStrictShape(
            bytes, MaxTransactionBytes, TenNinety.PivotFile);
        var transaction = StrictJsonIngestion.Deserialize<PivotTransaction>(
            bytes, TenNinety.PivotFile);
        Validate(transaction);
        return transaction;
    }

    private FileStream AcquireFileLock()
    {
        var path = Path + ".lock";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void Validate(PivotTransaction transaction)
    {
        if (!IsLowerHex(transaction.TransactionId, 32) ||
            !IsHexSha(transaction.ExpectedMainSha) ||
            !IsLowerHex(transaction.OldPlanSha256, 64) ||
            !IsLowerHex(transaction.OldStateSha256, 64) ||
            !IsLowerHex(transaction.NewPlanSha256, 64) ||
            !IsLowerHex(transaction.NewStateSha256, 64))
            throw new InvalidOperationException(
                $"{TenNinety.PivotFile} contains invalid or incomplete recovery evidence.");
    }

    private static bool IsHexSha(string value) =>
        value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
