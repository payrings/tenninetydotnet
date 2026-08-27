using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Core.Validation;
using Tenninety.Frontier;
using Tenninety.Git;

namespace Tenninety.Execution;

/// <summary>Applies an approved pivot diff to plan.json (Part IV.4 step 8): REWORK→PENDING, CANCEL→CANCELLED.</summary>
public static partial class PivotService
{
    public sealed class ApplyResult
    {
        public List<string> Reworked { get; } = new();
        public List<string> Cancelled { get; } = new();
        /// <summary>Jobs cancelled only because a dependency was cancelled.</summary>
        public List<string> CancelledByCascade { get; } = new();
        public List<string> Added { get; } = new();
        public int Kept { get; set; }
    }

    public static ApplyResult Apply(PivotProposal proposal, Plan plan, RuntimeState state)
    {
        // Build and validate on detached copies. A rejected proposal must not leave the caller's
        // in-memory plan/state half-mutated.
        var candidatePlan = Json.Deserialize<Plan>(Json.Serialize(plan));
        var candidateState = Json.Deserialize<RuntimeState>(Json.Serialize(state));
        var result = ApplyInPlace(proposal, candidatePlan, candidateState);

        plan.SchemaVersion = candidatePlan.SchemaVersion;
        plan.ProjectName = candidatePlan.ProjectName;
        plan.GlobalContext = candidatePlan.GlobalContext;
        plan.ArchitectureMap = candidatePlan.ArchitectureMap;
        plan.WorkPackages = candidatePlan.WorkPackages;
        state.CurrentWp = candidateState.CurrentWp;
        state.ExecutionMode = candidateState.ExecutionMode;
        state.Attempts = candidateState.Attempts;
        state.QueueStatus = candidateState.QueueStatus;
        state.Paused = candidateState.Paused;
        state.StopRequested = candidateState.StopRequested;
        state.SpecHash = candidateState.SpecHash;

        return result;
    }

    private static ApplyResult ApplyInPlace(PivotProposal proposal, Plan plan, RuntimeState state)
    {
        var result = new ApplyResult();
        var byId = plan.WorkPackages.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        var originalIds = byId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ---- 0. Classification exclusivity BEFORE anything is mutated -------------------
        // Every package must appear in exactly one of KEEP / REWORK / CANCEL.
        // (Cascade-cancelled dependents are derived later and exempt from this check.)
        var keepSet = proposal.Keep.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reworkSet = proposal.Rework.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cancelSet = proposal.Cancel.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overlaps = keepSet.Intersect(reworkSet).Concat(keepSet.Intersect(cancelSet))
            .Concat(reworkSet.Intersect(cancelSet)).Distinct().ToList();
        if (overlaps.Count > 0)
            throw new InvalidOperationException(
                $"pivot classifies {string.Join(", ", overlaps)} in more than one bucket.");

        foreach (var id in keepSet)
            if (!byId.ContainsKey(id))
                throw new InvalidOperationException($"pivot keep references unknown WP '{id}'.");
        foreach (var rework in proposal.Rework)
            if (!byId.ContainsKey(rework.Id))
                throw new InvalidOperationException($"pivot rework references unknown WP '{rework.Id}'.");
        foreach (var cancel in proposal.Cancel)
            if (!byId.ContainsKey(cancel.Id))
                throw new InvalidOperationException($"pivot cancel references unknown WP '{cancel.Id}'.");

        // ---- 1. Add NEW work packages before dependency closure --------------------------
        foreach (var newWp in proposal.NewWorkPackages)
        {
            if (byId.ContainsKey(newWp.Id))
                throw new InvalidOperationException($"pivot proposes duplicate WP id '{newWp.Id}'.");
            newWp.Status = TenNinety.WpStatus.Pending;
            plan.WorkPackages.Add(newWp);
            byId[newWp.Id] = newWp;
            result.Added.Add(newWp.Id);
        }

        // ---- 2. Cascade closure: cancelling a job cancels everything downstream ---------
        var cancelledSet = new HashSet<string>(cancelSet, StringComparer.OrdinalIgnoreCase);
        var cascaded = new List<string>();
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var wp in plan.WorkPackages)
            {
                if (cancelledSet.Contains(wp.Id)) continue;
                if (!wp.Dependencies.Any(d => cancelledSet.Contains(d))) continue;
                cancelledSet.Add(wp.Id);
                cascaded.Add(wp.Id);
                grew = true;
            }
        }

        var cancelledNew = proposal.NewWorkPackages
            .Where(w => cancelledSet.Contains(w.Id))
            .Select(w => w.Id)
            .ToList();
        if (cancelledNew.Count > 0)
            throw new InvalidOperationException(
                "new work packages depend on cancelled work: " + string.Join(", ", cancelledNew));

        // ---- 3. Classification completeness (after the closure is known) ----------------
        var unclassified = plan.WorkPackages
            .Where(w => originalIds.Contains(w.Id)
                     && !keepSet.Contains(w.Id)
                     && !reworkSet.Contains(w.Id)
                     && !cancelledSet.Contains(w.Id))
            .Select(w => w.Id).ToList();
        if (unclassified.Count > 0)
            throw new InvalidOperationException(
                "pivot leaves packages unclassified: " + string.Join(", ", unclassified));

        // ---- 4. Mutate ------------------------------------------------------------------
        foreach (var wp in plan.WorkPackages)
        {
            if (cancelSet.Contains(wp.Id) || cascaded.Contains(wp.Id))
            {
                wp.Status = TenNinety.WpStatus.Cancelled;
                state.Attempts.Remove(wp.Id);
                if (cancelSet.Contains(wp.Id)) result.Cancelled.Add(wp.Id);
                continue;
            }
            if (reworkSet.Contains(wp.Id))
            {
                var rework = proposal.Rework.Single(r => r.Id.Equals(wp.Id, StringComparison.OrdinalIgnoreCase));
                wp.Status = TenNinety.WpStatus.Pending;
                state.Attempts.Remove(wp.Id);
                if (rework.UpdatedDirectives.Count > 0)
                    wp.Directives = new List<string>(rework.UpdatedDirectives);
                // A pivot REWORK resolves AMBIGUOUS/CONFLICT markers (audit-stamped).
                wp.Notes = ResolveNotes(wp, rework.Reason);
                result.Reworked.Add(wp.Id);
                continue;
            }
            result.Kept++;
        }

        // The mutated graph must still be a valid strict DAG before it becomes truth again.
        var validation = PlanValidator.Validate(plan);
        if (!validation.IsValid)
            throw new InvalidOperationException(
                "pivot would invalidate the execution graph: " + string.Join("; ", validation.Errors));

        if (cascaded.Count > 0)
            result.CancelledByCascade.AddRange(cascaded);

        foreach (var wp in plan.WorkPackages)
            state.QueueStatus[wp.Id] = wp.Status;

        return result;
    }

    [GeneratedRegex(@"\b(?:CONFLICT|AMBIGUOUS)\b[:\-–—]?\s*", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerStrip();

    private static string ResolveNotes(WorkPackage wp, string reason)
    {
        var stripped = MarkerStrip().Replace(wp.Notes ?? "", "").Trim();
        var stamp = $"[resolved by pivot REWORK{(string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}]";
        return string.IsNullOrEmpty(stripped) ? stamp : $"{stamp} {stripped}";
    }
}

/// <summary>Hotfix flow for bad promotions (Part IV.5). Mechanical git revert + mechanical validation.</summary>
public sealed class RevertService
{
    private const string GitShowTruncationMarker = "… [git show truncated – showing head and tail] …";
    private readonly IGitService _git;
    private readonly TenNinetyConfig _config;
    private readonly IFrontierClient _frontier;
    private readonly ITesterAgent _tester;
    private readonly AuditLog _audit;
    private readonly Action<string>? _log;

    public RevertService(
        IGitService git, TenNinetyConfig config, IFrontierClient frontier,
        ITesterAgent tester, AuditLog audit, Action<string>? log = null)
    {
        _git = git;
        _config = config;
        _frontier = frontier;
        _tester = tester;
        _audit = audit;
        _log = log;
    }

    public sealed class RevertOutcome
    {
        public bool Success { get; init; }
        public string Message { get; init; } = "";
        public string? BranchLeftBehind { get; init; }
    }

    public async Task<RevertOutcome> RevertAsync(string shaOrRef, string reason, CancellationToken ct = default)
    {
        var commit = _git.FindCommit(shaOrRef);
        if (commit is null)
            return new RevertOutcome { Success = false, Message = $"commit '{shaOrRef}' not found." };

        // Exclusive workspace lock: a revert must never race an active daemon.
        using var _revertLock = DaemonLock.Acquire(_git.RepoPath);

        if (_git.CurrentBranch() != TenNinety.MainBranch)
            return new RevertOutcome
            {
                Success = false,
                Message = $"revert must start from '{TenNinety.MainBranch}', not '{_git.CurrentBranch()}'.",
            };

        if (!_git.IsClean())
            return new RevertOutcome { Success = false, Message = "working tree is not clean; refusing to revert." };

        var expectedMainSha = _git.HeadSha();

        if (!_git.IsAncestorOfMain(commit.Sha))
            return new RevertOutcome
            {
                Success = false,
                Message = $"commit {commit.Sha[..12]} is not on main – refusing to revert.",
            };

        var branch = $"{TenNinety.HotfixBranchPrefix}revert-{commit.Sha[..8]}";
        if (_git.BranchExists(branch))
            return new RevertOutcome
            {
                Success = false,
                Message = $"hotfix branch '{branch}' already exists; inspect or remove it before retrying.",
                BranchLeftBehind = branch,
            };
        _audit.Append("REVERT_STARTED", detail:
            $"{commit.Sha[..12]} reason={Core.Security.Sanitizer.SanitizeText(reason)}");
        _log?.Invoke($"reverting {commit.Sha[..12]} on branch '{branch}'");

        string diff;
        try
        {
            diff = await SafeShowAsync(commit.Sha, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var detail = Core.Security.Sanitizer.SanitizeText(ex.Message);
            _audit.Append("REVERT_ERROR", detail: detail);
            return new RevertOutcome
            {
                Success = false,
                Message = $"could not inspect the target commit safely: {detail}",
            };
        }
        if (diff.Contains(GitShowTruncationMarker, StringComparison.Ordinal))
        {
            _audit.Append("REVERT_ERROR", detail: "target commit exceeds bounded review input");
            return new RevertOutcome
            {
                Success = false,
                Message = "target commit is too large for complete automated revert analysis; manual intervention required.",
            };
        }

        // Frontier analyzes the commit and produces the revert patch guidance.
        var guidance = await _frontier.ProposeRevertAsync(
            new RevertRequest($"{commit.Sha}\n{commit.Subject}\n{commit.Author} {commit.Date}", diff, reason), ct);
        foreach (var step in guidance.Steps)
            _log?.Invoke($"frontier step: {step}");

        if (!guidance.MechanicalRevertSufficient)
            return new RevertOutcome
            {
                Success = false,
                Message = "frontier determined a mechanical revert is insufficient — manual intervention required.",
            };

        // Coder role: apply the mechanical revert on a hotfix branch.
        _git.CreateAndCheckoutBranch(branch);
        try
        {
            _git.RevertCommitNoEdit(commit.Sha);
            var expectedHotfixSha = _git.HeadSha();

            // Reviewer/Tester: validate the hotfix with the mechanical suite.
            var testCtx = new WpContext
            {
                RepoPath = _git.RepoPath,
                WorkPackage = new WorkPackage
                {
                    Id = "HOTFIX", Layer = "TEST", Title = $"Validate revert of {commit.Sha[..8]}",
                    Goal = "Mechanical validation of the revert.",
                    Directives = { "All tests must pass after revert." },
                    AcceptanceCriteria = { "Test suite exits 0." },
                },
                Attempt = 1,
            };
            var test = await _tester.RunTestsAsync(testCtx, ct);
            if (!test.Passed)
            {
                _git.CheckoutBranch(TenNinety.MainBranch);
                _audit.Append("REVERT_FAILED_TESTS", detail: commit.Sha[..12]);
                return new RevertOutcome
                {
                    Success = false,
                    Message = $"hotfix tests failed (exit {test.ExitCode}); branch '{branch}' left for inspection.",
                    BranchLeftBehind = branch,
                };
            }

            if (_git.CurrentBranch() != branch || _git.HeadSha() != expectedHotfixSha ||
                _git.FindCommit(TenNinety.MainBranch)?.Sha != expectedMainSha || !_git.IsClean())
                throw new InvalidOperationException(
                    "test command changed the mechanical revert or main; refusing unreviewed promotion.");

            var mergeSha = _git.SquashMergeToMain(branch, $"Revert \"{commit.Subject}\" [hotfix]");
            try { _git.DeleteBranchSafe(branch, force: true); } catch { /* non-fatal */ }
            _audit.Append("REVERT_PROMOTED", detail: mergeSha);
            _log?.Invoke($"revert promoted to main ({mergeSha[..12]})");
            return new RevertOutcome { Success = true, Message = $"reverted {commit.Sha[..12]} via {mergeSha[..12]}." };
        }
        catch (Exception ex)
        {
            try { _git.CheckoutBranch(TenNinety.MainBranch); } catch { /* already on main */ }
            _audit.Append("REVERT_ERROR", detail: Core.Security.Sanitizer.SanitizeText(ex.Message));
            return new RevertOutcome
            {
                Success = false,
                Message = $"revert failed: {ex.Message}; branch '{branch}' left for inspection.",
                BranchLeftBehind = branch,
            };
        }
    }

    private async Task<string> SafeShowAsync(string sha, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _git.RepoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "show", sha, "--format=fuller", "--patch", "--stat" },
        };
        ChildProcessEnvironment.ApplyAllowlist(psi);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start git show.");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));
        var stdoutTask = ReadBoundedAsync(proc.StandardOutput, timeoutCts.Token);
        var stderrTask = ReadBoundedAsync(proc.StandardError, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
            var output = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"git show failed (exit {proc.ExitCode}): {Truncate(stderr, 300)}");
            return output;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { await proc.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("git show timed out after 2 minutes.");
        }
        finally
        {
            if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        const int maxChars = 50_000;
        const int headChars = 37_500;
        const int tailChars = maxChars - headChars;
        var head = new StringBuilder(headChars);
        var tail = new StringBuilder(tailChars);
        var buffer = new char[4096];
        long total = 0;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            total += read;
            var offset = 0;
            if (head.Length < headChars)
            {
                var take = Math.Min(read, headChars - head.Length);
                head.Append(buffer, 0, take);
                offset = take;
            }
            if (offset >= read) continue;
            tail.Append(buffer, offset, read - offset);
            if (tail.Length > tailChars) tail.Remove(0, tail.Length - tailChars);
        }

        return total <= maxChars
            ? head.Append(tail).ToString()
            : head + $"\n{GitShowTruncationMarker}\n" + tail;
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
