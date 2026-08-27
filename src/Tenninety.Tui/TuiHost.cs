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
    private static string? _banner;

    public static async Task<int> RunAsync(
        Workspace ws, Plan plan, RuntimeState state, Orchestrator orchestrator)
    {
        _banner = null;
        var tester = new AgentFactory(ws.Config).CreateTester(line => ws.Audit.Append("TESTER", detail: line));
        var revertService = new RevertService(ws.Git, ws.Config, ws.CreateFrontier(), tester, ws.Audit,
            log: line => ws.Audit.Append("REVERT", detail: line));
        var frontier = ws.CreateFrontier();

        var runCts = new CancellationTokenSource();
        Task<OrchestratorExit> runTask = orchestrator.RunAsync(runCts.Token);
        var quit = false;

        async Task EnsureIdleAsync()
        {
            if (!runTask.IsCompleted)
            {
                orchestrator.Pause();
                _banner = "pausing daemon for safe interaction…";
                await SafeAwait(runTask);
            }
        }

        while (!quit)
        {
            Draw(ws, plan, state);
            if (!Console.KeyAvailable)
            {
                await Task.Delay(400);
                continue;
            }

            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.P:
                    if (runTask.IsCompleted)
                    {
                        runCts.Dispose();
                        runCts = new CancellationTokenSource();
                        orchestrator.Resume();
                        runTask = orchestrator.RunAsync(runCts.Token);
                        _banner = "resumed";
                    }
                    else
                    {
                        orchestrator.Pause();
                        _banner = "pausing…";
                        Draw(ws, plan, state);
                        await SafeAwait(runTask);
                        _banner = "PAUSED — [P] resume · [S] pivot · [R] revert";
                    }
                    break;

                case ConsoleKey.S:
                    await EnsureIdleAsync();
                    _banner = await LockedPivotFlowAsync(ws, plan, state, frontier);
                    break;

                case ConsoleKey.R:
                    await EnsureIdleAsync();
                    _banner = await RevertFlowAsync(ws, revertService);
                    break;

                case ConsoleKey.L:
                    ShowLogs(ws);
                    break;

                case ConsoleKey.Q:
                    quit = true;
                    break;
            }
        }

        if (!runTask.IsCompleted)
        {
            orchestrator.RequestStop();
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                runCts.Cancel();
                try { await runTask; } catch { runCts.Dispose(); return 1; }
            }
            catch
            {
                runCts.Dispose();
                return 1;
            }
        }

        try
        {
            var code = ExitCode(await runTask);
            runCts.Dispose();
            return code;
        }
        catch
        {
            runCts.Dispose();
            return 1;
        }
    }

    /// <summary>Observes a run task so an interactive action can display faults without crashing.</summary>
    private static async Task SafeAwait(Task<OrchestratorExit> task)
    {
        try { await task; }
        catch (Exception ex)
        {
            _banner = $"daemon error: {ex.Message}";
        }
    }

    private static int ExitCode(OrchestratorExit exit) =>
        exit == OrchestratorExit.Deadlocked ? 4 : exit == OrchestratorExit.Cancelled ? 1 : 0;

    private static void Draw(Workspace ws, Plan plan, RuntimeState state)
    {
        Console.Clear();
        AnsiConsole.Write(new Rule($"[b]10/90 tenninety[/] [grey]v{TenNinety.SchemaVersion} — {Markup.Escape(plan.ProjectName)}[/]")
            .RuleStyle("grey"));

        var health = new Grid();
        for (var i = 0; i < 4; i++) health.AddColumn();
        var branch = Safe(() => ws.Git.CurrentBranch(), "?");
        var clean = Safe(() => ws.Git.IsClean().ToString().ToLowerInvariant(), "?");
        var specHash = File.Exists(ws.SpecPath)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ws.SpecPath)))[..8].ToLowerInvariant()
            : "n/a";
        health.AddRow(
            new Markup($"[b]Mode[/] {state.ExecutionMode}{(state.Paused ? " [red](paused)[/]" : "")}"),
            new Markup($"[b]Provider[/] {Markup.Escape(ws.Config.ProviderMode)}{(ws.Config.UseLlamaSwap ? " + llama-swap" : "")}"),
            new Markup($"[b]Models[/] {Markup.Escape(ws.Config.LocalModels.Coder)} / {Markup.Escape(ws.Config.LocalModels.Reviewer)}"),
            new Markup($"[b]Spec hash[/] {specHash}"));
        health.AddRow(
            new Markup($"[b]Branch[/] {Markup.Escape(branch)}"),
            new Markup($"[b]Git[/] {(clean == "true" ? "[green]clean[/]" : "[red]dirty[/]")}"),
            new Markup($"[b]Frontier[/] {Markup.Escape(ws.Config.FrontierEndpoint)}"),
            new Markup($"[b]Current WP[/] {Markup.Escape(state.CurrentWp ?? "-")}"));
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
                $"[{color}]{Markup.Escape("[" + status + "]")}[/]",
                Markup.Escape(wp.Id),
                Markup.Escape(wp.Layer),
                Markup.Escape(wp.Title) +
                (Core.Validation.WpMarkers.IsConflict(wp) ? " [red]⚠CONFLICT[/]"
                    : Core.Validation.WpMarkers.IsAmbiguous(wp) ? " [yellow]⚠AMBIGUOUS[/]" : ""),
                attempts);
        }
        AnsiConsole.Write(table);

        if (_banner is not null)
            AnsiConsole.MarkupLine($"\n[yellow]{Markup.Escape(_banner)}[/]");

        AnsiConsole.MarkupLine(
            "\n[grey][[P]][/] Pause/Resume  [grey][[S]][/] Snapshot & Pivot  [grey][[R]][/] Revert  " +
            "[grey][[L]][/] View Logs  [grey][[Q]][/] Quit");
    }

    private static async Task<string> LockedPivotFlowAsync(
        Workspace ws, Plan plan, RuntimeState state, IFrontierClient frontier)
    {
        IDisposable workspaceLock;
        try
        {
            workspaceLock = DaemonLock.Acquire(ws.Root);
        }
        catch (Exception ex)
        {
            return $"cannot start pivot: {ex.Message}";
        }

        using (workspaceLock)
        {
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

            return await PivotFlowUnderLockAsync(ws, plan, state, frontier);
        }
    }

    private static async Task<string> PivotFlowUnderLockAsync(
        Workspace ws, Plan plan, RuntimeState state, IFrontierClient frontier)
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
            proposal = await frontier.ProposePivotAsync(snapshot);
        }
        catch (Exception ex)
        {
            return $"pivot analysis failed: {ex.Message}";
        }

        Console.Clear();
        AnsiConsole.Write(new Rule("[b]Frontier pivot diff[/]").RuleStyle("aqua"));
        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(proposal.Rationale)}[/]\n");
        var grid = new Grid();
        grid.AddColumn(); grid.AddColumn();
        grid.AddRow("[green]KEEP:[/]   " + proposal.Keep.Count, TruncateIds(proposal.Keep));
        grid.AddRow("[yellow]REWORK:[/] " + proposal.Rework.Count,
            TruncateIds(proposal.Rework.Select(r => r.Id)) +
            (proposal.Rework.Count > 0 ? $"  [grey]{Markup.Escape(proposal.Rework[0].Reason)}[/]" : ""));
        grid.AddRow("[red]CANCEL:[/]  " + proposal.Cancel.Count,
            TruncateIds(proposal.Cancel.Select(c => c.Id)) +
            (proposal.Cancel.Count > 0 ? $"  [grey]{Markup.Escape(proposal.Cancel[0].Reason)}[/]" : ""));
        grid.AddRow("[aqua]NEW:[/]     " + proposal.NewWorkPackages.Count,
            TruncateIds(proposal.NewWorkPackages.Select(w => w.Id)));
        AnsiConsole.Write(grid);

        if (!AnsiConsole.Confirm("\nApply this pivot?", false))
            return "Pivot discarded — daemon still paused ([P] to resume).";

        var result = PivotService.Apply(proposal, plan, state);
        ws.Plans.Save(plan);
        ws.States.Save(state);
        ws.Git.CommitPaths([TenNinety.StateDir + "/" + TenNinety.PlanFile], "pivot applied");
        ws.Audit.Append("PIVOT_APPLIED",
            detail: $"kept={result.Kept} rework=[{string.Join(",", result.Reworked)}] " +
                    $"cancel=[{string.Join(",", result.Cancelled)}] added=[{string.Join(",", result.Added)}]");
        return "Pivot applied. Press [P] to resume execution.";
    }

    private static async Task<string> RevertFlowAsync(Workspace ws, RevertService service)
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[b]Revert a promotion[/]").RuleStyle("red"));
        AnsiConsole.MarkupLine("[dim]Recent commits on main (newest first):[/]\n");

        var commits = Safe(() => ws.Git.RecentCommits(10), new List<GitCommit>());
        for (var i = 0; i < commits.Count; i++)
            AnsiConsole.MarkupLine($"  [{(i == 0 ? "yellow" : "white")}]({i})[/] [grey]{Markup.Escape(commits[i].Sha[..10])}[/] {Markup.Escape(commits[i].Subject)}");

        if (commits.Count == 0)
            return "no commits found on main.";

        var selection = AnsiConsole.Prompt(new TextPrompt<string>("[b]Commit to revert (sha or index):[/]")
            .DefaultValue("0"));
        GitCommit target;
        if (selection.Length > 0 && selection.All(char.IsDigit) && int.Parse(selection) < commits.Count)
            target = commits[int.Parse(selection)];
        else
        {
            var found = commits.FirstOrDefault(c => c.Sha.StartsWith(selection, StringComparison.OrdinalIgnoreCase));
            if (found is null)
                return $"commit '{selection}' not among recent commits.";
            target = found;
        }

        var reason = AnsiConsole.Ask<string>("Reason [optional]:", "");
        AnsiConsole.MarkupLine("\n[dim]Running hotfix flow (frontier guidance → mechanical revert → tests → merge)…[/]");
        var outcome = await service.RevertAsync(target.Sha, reason, CancellationToken.None);
        return outcome.Message;
    }

    private static void ShowLogs(Workspace ws)
    {
        Console.Clear();
        AnsiConsole.Write(new Rule("[b]Audit log (tail 30)[/]"));
        foreach (var e in ws.Audit.ReadTail(30))
            AnsiConsole.MarkupLine(
                $"[grey]{Markup.Escape(e.Timestamp.Length >= 13 ? e.Timestamp[^13..] : e.Timestamp)}[/] " +
                $"[b]{Markup.Escape(e.Event)}[/]" +
                (string.IsNullOrEmpty(e.WorkPackageId) ? "" : $" {Markup.Escape(e.WorkPackageId)}") +
                (string.IsNullOrEmpty(e.Detail) ? "" : $" [grey]{Markup.Escape(e.Detail)}[/]"));
        AnsiConsole.MarkupLine("\n[grey]Press any key to return to the dashboard…[/]");
        Console.ReadKey(intercept: true);
    }

    private static string TruncateIds(IEnumerable<string> ids)
    {
        var list = ids.ToList();
        const int max = 8;
        var shown = string.Join(", ", list.Take(max));
        return Markup.Escape(list.Count > max ? shown + $", …(+{list.Count - max})" : shown);
    }

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
    }
}
