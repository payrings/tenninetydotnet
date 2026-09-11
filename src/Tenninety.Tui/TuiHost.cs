using Spectre.Console;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Execution;
using Tenninety.Frontier;
using Tenninety.Git;
using GitCommit = Tenninety.Git.GitCommit;
using SpectreValidationResult = Spectre.Console.ValidationResult;

namespace Tenninety.Tui;

/// <summary>
/// Real-time supervisor dashboard (Part V): queue view, system health, controls
/// [P] Pause/Resume · [S] Snapshot &amp; Pivot · [R] Revert · [L] Logs · [Q] Quit.
/// The orchestrator runs as a background task; interactive dialogs pause the daemon at its next safe point.
/// </summary>
public static class TuiHost
{
    public static async Task<int> RunAsync(
        Workspace ws, Plan plan, RuntimeState state, Orchestrator orchestrator)
    {
        using var execution = new TuiExecution(
            orchestrator.RunAsync, orchestrator.Pause, orchestrator.ResumeAsync,
            orchestrator.RequestStop, orchestrator.ClearControlRequestsIfIdle);
        var tester = new AgentFactory(ws.Config).CreateTester(ws.Git, line => ws.Audit.Append("TESTER", detail: line));
        var revertService = new RevertService(ws.Git, ws.Config, ws.CreateFrontier(), tester, ws.Audit,
            log: line => ws.Audit.Append("REVERT", detail: line));
        var frontier = ws.CreateFrontier();

        var quit = false;
        using var interactionCts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            Volatile.Write(ref quit, true);
            execution.RequestShutdown();
            interactionCts.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            while (!Volatile.Read(ref quit))
            {
                await execution.ObserveCompletedAsync();
                Draw(ws, plan, state, execution);
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(400, interactionCts.Token);
                    continue;
                }

                switch (Console.ReadKey(intercept: true).Key)
                {
                    case ConsoleKey.P:
                        if (!execution.IsRunning)
                            await execution.ResumeAsync();
                        else
                        {
                            var pauseTask = execution.PauseAsync();
                            Draw(ws, plan, state, execution);
                            await pauseTask;
                        }
                        break;

                    case ConsoleKey.S:
                        await execution.PauseAsync();
                        execution.Banner = await LockedPivotFlowAsync(
                            ws, plan, state, frontier, interactionCts.Token);
                        break;

                    case ConsoleKey.R:
                        await execution.PauseAsync();
                        execution.Banner = await RevertFlowAsync(
                            ws, revertService, interactionCts.Token);
                        break;

                    case ConsoleKey.L:
                        ShowLogs(ws);
                        break;

                    case ConsoleKey.Q:
                        Volatile.Write(ref quit, true);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (interactionCts.IsCancellationRequested)
        {
            Volatile.Write(ref quit, true);
        }
        finally
        {
            try { await execution.ShutdownAsync(); }
            finally
            {
                Console.CancelKeyPress -= onCancel;
            }
        }

        WriteExecutionStatus(AnsiConsole.Console, execution);
        return execution.ExitCode;
    }

    internal static void WriteExecutionStatus(IAnsiConsole console, TuiExecution execution)
    {
        var color = execution.Status switch
        {
            "FAILED" or "DEADLOCKED" or "CANCELLED" => "red",
            "COMPLETED" => "green",
            "RUNNING" => "aqua",
            _ => "yellow",
        };
        console.MarkupLine($"\n[b]Execution[/] [{color}]{execution.Status}[/]");
        if (execution.Banner is not null)
            console.MarkupLine($"[yellow]{Markup.Escape(Diagnostic(execution.Banner))}[/]");
        if (execution.LastError is not null)
            console.MarkupLine($"[red]Last execution failure: {Markup.Escape(execution.LastError)}[/]");
    }

    private static void Draw(Workspace ws, Plan plan, RuntimeState state, TuiExecution execution)
    {
        Console.Clear();
        AnsiConsole.Write(new Rule(
                $"[b]10/90 tenninety[/b] — {Markup.Escape(Diagnostic(plan.ProjectName, 512))}")
            .RuleStyle("grey"));

        var health = new Grid();
        for (var i = 0; i < 4; i++) health.AddColumn();
        var branch = Safe(() => ws.Git.CurrentBranch(), "?");
        var clean = Safe(() => ws.Git.IsClean().ToString().ToLowerInvariant(), "?");
        var specHash = File.Exists(ws.SpecPath)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ws.SpecPath)))[..8].ToLowerInvariant()
            : "n/a";
        health.AddRow(
            new Markup($"[b]Mode[/] {Markup.Escape(Diagnostic(state.ExecutionMode, 64))}"),
            new Markup($"[b]Provider[/] {Markup.Escape(Diagnostic(ws.Config.ProviderMode, 128))}" +
                       (ws.Config.UseLlamaSwap ? " + llama-swap" : "")),
            new Markup($"[b]Models[/] {Markup.Escape(Diagnostic(ws.Config.LocalModels.Coder, 256))} / " +
                       Markup.Escape(Diagnostic(ws.Config.LocalModels.Reviewer, 256))),
            new Markup($"[b]Spec hash[/] {specHash}"));
        health.AddRow(
            new Markup($"[b]Branch[/] {Markup.Escape(Diagnostic(branch, 512))}"),
            new Markup($"[b]Git[/] {(clean == "true" ? "[green]clean[/]" : "[red]dirty[/]")}"),
            new Markup($"[b]Frontier[/] {Markup.Escape(Diagnostic(ws.Config.FrontierEndpoint, 512))}"),
            new Markup($"[b]Current WP[/] {Markup.Escape(Diagnostic(state.CurrentWp ?? "-", 256))}"));
        AnsiConsole.Write(health);

        var table = new Table().Border(TableBorder.Rounded)
            .Title("Queue")
            .Caption("[grey]Attempts shown as phase-count/max (total)[/]");
        table.AddColumns("[b]Status[/]", "WP", "Layer", "Title", "Attempts");
        foreach (var wp in plan.WorkPackages.OrderBy(w => Core.Validation.PlanValidator.IdOrder(w.Id)))
        {
            var status = state.QueueStatus.TryGetValue(wp.Id, out var queued) ? queued : wp.Status;
            state.Attempts.TryGetValue(wp.Id, out var info);
            var attempts = info is null
                ? "-"
                : status == TenNinety.WpStatus.Blocked
                    ? $"{info.Total} total"
                    : $"{info.Count}/{info.Max} ({info.Total})";
            var color = status switch
            {
                TenNinety.WpStatus.Done => "green",
                TenNinety.WpStatus.Active => "aqua",
                TenNinety.WpStatus.Blocked => "red",
                TenNinety.WpStatus.Cancelled => "grey",
                _ => "white",
            };
            table.AddRow(
                $"[{color}]{Markup.Escape("[" + Diagnostic(status, 64) + "]")}[/]",
                Markup.Escape(Diagnostic(wp.Id, 256)),
                Markup.Escape(Diagnostic(wp.Layer, 256)),
                Markup.Escape(Diagnostic(wp.Title, 1000)) +
                (Core.Validation.WpMarkers.IsConflict(wp) ? " [red]⚠CONFLICT[/]"
                    : Core.Validation.WpMarkers.IsAmbiguous(wp) ? " [yellow]⚠AMBIGUOUS[/]" : ""),
                attempts);
        }
        AnsiConsole.Write(table);

        WriteExecutionStatus(AnsiConsole.Console, execution);

        AnsiConsole.MarkupLine(
            "\n[grey][[P]][/] Pause/Resume  [grey][[S]][/] Snapshot & Pivot  [grey][[R]][/] Revert  " +
            "[grey][[L]][/] View Logs  [grey][[Q]][/] Quit");
    }

    private static async Task<string> LockedPivotFlowAsync(
        Workspace ws, Plan plan, RuntimeState state, IFrontierClient frontier,
        CancellationToken ct)
    {
        DaemonLockLease workspaceLock;
        try
        {
            workspaceLock = DaemonLock.Acquire(ws.Root);
        }
        catch (Exception ex)
        {
            return Diagnostic($"cannot start pivot: {ex.Message}");
        }

        using (workspaceLock)
        {
            var pivotPersistence = new PivotPersistence(ws.Git, ws.Plans, ws.States);
            try
            {
                pivotPersistence.RecoverPending(workspaceLock);
            }
            catch (Exception ex)
            {
                return Diagnostic($"cannot recover an interrupted pivot: {ex.Message}");
            }
            if (RuntimeGitignoreMigration.CountPromotionJournalsInOtherWorktrees(ws.Git) > 0)
                return "a linked worktree owns pending promotion recovery evidence; run " +
                       "'tenninety start' from that worktree before applying a pivot.";
            if (new PromotionTransactionStore(
                    Path.Combine(ws.Root, TenNinety.StateDir, TenNinety.PromotionFile)).Exists())
                return "a promotion recovery transaction is pending; run 'tenninety start' " +
                       "to reconcile it before applying a pivot.";
            if (ws.Git.CurrentBranch() != TenNinety.MainBranch || !ws.Git.IsClean())
                return "pivot requires a clean workspace on main.";

            CopyPlan(ws.LoadPlan(), plan);
            CopyState(ws.States.Load(), state);
            foreach (var wp in plan.WorkPackages)
            {
                if (state.QueueStatus.TryGetValue(wp.Id, out var status) &&
                    status is TenNinety.WpStatus.Done or TenNinety.WpStatus.Blocked or TenNinety.WpStatus.Cancelled)
                    wp.Status = status;
                else
                {
                    wp.Status = TenNinety.WpStatus.Pending;
                    state.QueueStatus[wp.Id] = TenNinety.WpStatus.Pending;
                }
            }

            return await PivotFlowUnderLockAsync(
                ws, plan, state, frontier, pivotPersistence, workspaceLock, ct);
        }
    }

    private static async Task<string> PivotFlowUnderLockAsync(
        Workspace ws, Plan plan, RuntimeState state, IFrontierClient frontier,
        PivotPersistence persistence, DaemonLockLease daemonLock, CancellationToken ct)
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[b]Snapshot & Pivot[/]").RuleStyle("aqua"));

        var intent = AnsiConsole.Prompt(
            new TextPrompt<string>("[b]Describe the change you want to make:[/]")
                .Validate(t => t.Trim().Length >= 4
                    ? SpectreValidationResult.Success()
                    : SpectreValidationResult.Error("[red]describe the intent in a few words.[/]")));

        var snapshot = new PivotRequest(
            SpecSnapshot: File.Exists(ws.SpecPath) ? File.ReadAllText(ws.SpecPath) : "(spec.md unavailable)",
            PlanJson: Json.Serialize(plan),
            UserIntent: intent,
            AuditTail: string.Join("\n", ws.Audit.ReadTail(20)
                .Select(e => $"{e.Timestamp} {e.Event} {e.WorkPackageId} {e.Detail}")));

        AnsiConsole.MarkupLine("\n[dim]Sending to Frontier…[/]");
        PivotProposal proposal;
        try
        {
            proposal = await frontier.ProposePivotAsync(snapshot, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Diagnostic($"pivot analysis failed: {ex.Message}");
        }

        Console.Clear();
        AnsiConsole.Write(new Rule("[b]Frontier pivot diff[/]").RuleStyle("aqua"));
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(Diagnostic(proposal.Rationale))}[/]\n");
        var grid = new Grid();
        grid.AddColumn(); grid.AddColumn();
        grid.AddRow("[green]KEEP:[/]   " + proposal.Keep.Count, TruncateIds(proposal.Keep));
        grid.AddRow("[yellow]REWORK:[/] " + proposal.Rework.Count,
            TruncateIds(proposal.Rework.Select(r => r.Id)) +
            (proposal.Rework.Count > 0
                ? $"  [grey]{Markup.Escape(Diagnostic(proposal.Rework[0].Reason, 1000))}[/]"
                : ""));
        grid.AddRow("[red]CANCEL:[/]  " + proposal.Cancel.Count,
            TruncateIds(proposal.Cancel.Select(c => c.Id)) +
            (proposal.Cancel.Count > 0
                ? $"  [grey]{Markup.Escape(Diagnostic(proposal.Cancel[0].Reason, 1000))}[/]"
                : ""));
        grid.AddRow("[aqua]NEW:[/]     " + proposal.NewWorkPackages.Count,
            TruncateIds(proposal.NewWorkPackages.Select(w => w.Id)));
        AnsiConsole.Write(grid);

        if (!AnsiConsole.Confirm("\nApply this pivot?", false))
            return "Pivot discarded — daemon still paused ([P] to resume).";

        PivotService.ApplyResult result;
        try
        {
            result = persistence.ApplyApproved(proposal, plan, state, daemonLock);
        }
        catch (Exception ex)
        {
            return Diagnostic($"pivot was not completed: {ex.Message}");
        }
        ws.Audit.Append("PIVOT_APPLIED",
            detail: $"kept={result.Kept} rework=[{string.Join(",", result.Reworked)}] " +
                    $"cancel=[{string.Join(",", result.Cancelled)}] added=[{string.Join(",", result.Added)}]");
        return "Pivot applied. Press [P] to resume execution.";
    }

    private static async Task<string> RevertFlowAsync(
        Workspace ws, RevertService service, CancellationToken ct)
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[b]Revert a promotion[/]").RuleStyle("red"));
        AnsiConsole.MarkupLine("[dim]Recent commits on main (newest first):[/]\n");

        var commits = Safe(() => ws.Git.RecentCommits(10), new List<GitCommit>());
        for (var i = 0; i < commits.Count; i++)
            AnsiConsole.MarkupLine(
                $"  [{(i == 0 ? "yellow" : "white")}]({i})[/] " +
                $"[grey]{Markup.Escape(commits[i].Sha[..10])}[/] " +
                Markup.Escape(Diagnostic(commits[i].Subject, 1000)));

        if (commits.Count == 0)
            return "no commits found on main.";

        var selection = AnsiConsole.Prompt(new TextPrompt<string>("[b]Commit to revert (sha or index):[/]")
            .DefaultValue("0"));
        return await ResolveAndRevertAsync(selection, commits, async (target, token) =>
        {
            var reason = AnsiConsole.Ask<string>("Reason [optional]:", "");
            AnsiConsole.MarkupLine("\n[dim]Running hotfix flow (frontier guidance → mechanical revert → tests → merge)…[/]");
            var outcome = await service.RevertAsync(target.Sha, reason, token);
            return Diagnostic(outcome.Message);
        }, ct);
    }

    internal static bool TryResolveRevertSelection(
        string selection,
        IReadOnlyList<GitCommit> commits,
        out GitCommit? target,
        out string error)
    {
        target = null;
        error = "";
        if (string.IsNullOrWhiteSpace(selection))
        {
            error = "commit selection cannot be blank.";
            return false;
        }

        // A full SHA remains exact even when all 40 characters happen to be decimal digits.
        var exact = selection.Length == 40
            ? commits.FirstOrDefault(c =>
                string.Equals(c.Sha, selection, StringComparison.OrdinalIgnoreCase))
            : null;
        if (exact is not null)
        {
            target = exact;
            return true;
        }

        if (selection.All(c => c is >= '0' and <= '9'))
        {
            if (!int.TryParse(selection, out var index) || index < 0 || index >= commits.Count)
            {
                error = $"commit index '{selection}' is outside the displayed range.";
                return false;
            }
            target = commits[index];
            return true;
        }

        if (selection.Length > 40 ||
            !selection.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
        {
            error = "commit SHA must contain 1 to 40 ASCII hexadecimal characters.";
            return false;
        }

        var matches = commits
            .Where(c => c.Sha.StartsWith(selection, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        if (matches.Count == 1)
        {
            target = matches[0];
            return true;
        }

        error = matches.Count == 0
            ? $"commit '{selection}' not among recent commits."
            : $"commit prefix '{selection}' is ambiguous among recent commits.";
        return false;
    }

    internal static async Task<string> ResolveAndRevertAsync(
        string selection,
        IReadOnlyList<GitCommit> commits,
        Func<GitCommit, CancellationToken, Task<string>> revert,
        CancellationToken ct)
    {
        if (!TryResolveRevertSelection(selection, commits, out var target, out var error))
            return Diagnostic(error);
        return await revert(target!, ct);
    }

    private static void ShowLogs(Workspace ws)
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[b]Audit log (tail 30)[/]"));
        foreach (var e in ws.Audit.ReadTail(30))
            AnsiConsole.MarkupLine(
                $"[grey]{Markup.Escape(Diagnostic(e.Timestamp.Length >= 13 ? e.Timestamp[^13..] : e.Timestamp, 64))}[/] " +
                $"[b]{Markup.Escape(Diagnostic(e.Event, 128))}[/]" +
                (string.IsNullOrEmpty(e.WorkPackageId)
                    ? ""
                    : $" {Markup.Escape(Diagnostic(e.WorkPackageId, 256))}") +
                (string.IsNullOrEmpty(e.Detail)
                    ? ""
                    : $" [grey]{Markup.Escape(Diagnostic(e.Detail))}[/]"));
        AnsiConsole.MarkupLine("\n[grey]Press any key to return to the dashboard…[/]");
        Console.ReadKey(intercept: true);
    }

    private static string TruncateIds(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        const int max = 8;
        var shown = string.Join(", ", list.Take(max).Select(id => Diagnostic(id, 256)));
        return Markup.Escape(list.Count > max ? shown + $", …(+{list.Count - max})" : shown);
    }

    private static string Diagnostic(string? value, int maxChars = 4000) =>
        Core.Security.Sanitizer.SanitizeDiagnostic(value ?? "", maxChars);

    private static T Safe<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch { return fallback; }
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
}
