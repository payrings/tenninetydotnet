using Spectre.Console;
using Tenninety.Core;
using Tenninety.Core.Security;
using Tenninety.Execution;

namespace Tenninety.Cli.Commands;

public static class InitCommand
{
    public static int Run()
    {
        var root = Directory.GetCurrentDirectory();
        var stateDir = Path.Combine(root, TenNinety.StateDir);
        var configExisted = File.Exists(Path.Combine(stateDir, TenNinety.ConfigFile));
        var ignoreExisted = File.Exists(Path.Combine(stateDir, ".gitignore"));
        var specExisted = File.Exists(Path.Combine(root, TenNinety.SpecFile));
        var ws = Workspace.Create();
        var commitPaths = new List<string>();
        if (!ws.Git.IsRepository())
        {
            ws.Git.Init();
            AnsiConsole.MarkupLine("[green]Initialized git repository on branch 'main'.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]Git repository already present.[/]");
        }

        if (!configExisted)
        {
            ws.Configs.Save(ws.Config);
            commitPaths.Add($"{TenNinety.StateDir}/{TenNinety.ConfigFile}");
            AnsiConsole.MarkupLine($"[green]Wrote {TenNinety.StateDir}/{TenNinety.ConfigFile}.[/]");
        }

        if (!ignoreExisted)
            commitPaths.Add($"{TenNinety.StateDir}/.gitignore");

        if (!specExisted)
        {
            File.WriteAllText(ws.SpecPath, SampleSpec);
            commitPaths.Add(TenNinety.SpecFile);
            AnsiConsole.MarkupLine($"[green]Wrote starter {TenNinety.SpecFile} — replace it with your real spec.[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[dim]{TenNinety.SpecFile} already present.[/]");
        }

        // Commit only files created by this command; never capture unrelated user work.
        ws.Git.CommitPaths(commitPaths, "chore: initialize 10/90 framework state (.tenninety)");

        var table = new Table().Border(TableBorder.Rounded).Title("10/90 tenninety — initialized");
        table.AddColumns("Next step", "Command");
        table.AddRow("1. Write your spec", $"edit ./{TenNinety.SpecFile}");
        table.AddRow("2. Generate execution graph", "tenninety plan --spec ./spec.md");
        table.AddRow("3. Review plan.json", "tenninety status");
        table.AddRow("4. Start autonomous execution", "tenninety start");
        AnsiConsole.Write(table);

        if (ws.Config.ProviderMode.Equals("mock", StringComparison.OrdinalIgnoreCase))
            AnsiConsole.MarkupLine(
                "[yellow]provider_mode=mock:[/] frontier + local agents are simulated offline. " +
                $"Set provider_mode and {Markup.Escape(ws.Config.FrontierApiKeyEnv)} for live models.");
        return 0;
    }

    private const string SampleSpec = """
        # My Project

        ## Business Rules
        - Describe the domain workflows here.

        ## Technical Hints
        - Tech stack, data stores, API shape.

        ## UI Descriptions (optional)
        - Textual wireframes for frontend work packages.
        """;
}

public static class StatusCommand
{
    public static int Run()
    {
        var ws = Workspace.Load();
        if (!ws.Plans.Exists())
        {
            AnsiConsole.MarkupLine("[yellow]No plan.json yet.[/] Run 'tenninety plan --spec ./spec.md' first.");
            return 1;
        }

        var plan = ws.Plans.Load();
        var state = ws.States.Load();
        RenderDashboard(ws, plan, state);
        return 0;
    }

    public static void RenderDashboard(Workspace ws, Core.Models.Plan plan, Core.Models.RuntimeState state)
    {
        var health = new Grid();
        health.AddColumn(); health.AddColumn(); health.AddColumn(); health.AddColumn();
        health.AddRow(
            new Markup($"[b]Project[/] {Markup.Escape(plan.ProjectName)}"),
            new Markup($"[b]Mode[/] {Markup.Escape(state.ExecutionMode)}{(state.Paused ? " [red](PAUSED)[/]" : "")}"),
            new Markup($"[b]Provider[/] {Markup.Escape(ws.Config.ProviderMode)}{(ws.Config.UseLlamaSwap ? " + llama-swap" : "")}"),
            new Markup($"[b]Models[/] {Markup.Escape(ws.Config.LocalModels.Coder)} / {Markup.Escape(ws.Config.LocalModels.Reviewer)}"));
        health.AddRow(
            new Markup($"[b]Branch[/] {Markup.Escape(ws.Git.CurrentBranch())}"),
            new Markup($"[b]Tree[/] {(ws.Git.IsClean() ? "[green]clean[/]" : "[red]dirty[/]")}"),
            new Markup($"[b]Spec hash[/] {SpecHash(ws)}"),
            new Markup($"[b]Frontier[/] {Markup.Escape(ws.Config.FrontierEndpoint)}"));

        var table = new Table().Border(TableBorder.Rounded)
            .Title($"Queue — {plan.WorkPackages.Count} work packages");
        table.AddColumns("Status", "WP", "Layer", "Title", "Attempts");
        foreach (var wp in plan.WorkPackages.OrderBy(w => Core.Validation.PlanValidator.IdOrder(w.Id)))
        {
            // Runtime truth lives in state.json queue_status; plan.json is the graph itself.
            var status = state.QueueStatus.TryGetValue(wp.Id, out var queued) ? queued : wp.Status;
            state.Attempts.TryGetValue(wp.Id, out var info);
            var attempts = info is null
                ? "-"
                : status == Core.TenNinety.WpStatus.Blocked
                    ? $"{info.Total} total"
                    : $"{info.Count}/{info.Max} ({info.Total})";
            var color = status switch
            {
                Core.TenNinety.WpStatus.Done => "green",
                Core.TenNinety.WpStatus.Active => "aqua",
                Core.TenNinety.WpStatus.Blocked => "red",
                Core.TenNinety.WpStatus.Cancelled => "grey",
                _ => "white",
            };
            table.AddRow(
                $"[{color}]{Markup.Escape("[" + status + "]")}[/]",
                Markup.Escape(wp.Id),
                Markup.Escape(wp.Layer),
                Markup.Escape(wp.Title) + FlagSuffix(wp),
                attempts);
        }

        AnsiConsole.Write(health);
        AnsiConsole.Write(table);
    }

    /// <summary>Blueprint v3.2 Enterprise: visible marker for AMBIGUOUS/CONFLICT packages.</summary>
    internal static string FlagSuffix(Core.Models.WorkPackage wp)
    {
        if (Core.Validation.WpMarkers.IsConflict(wp)) return " [red]⚠CONFLICT[/]";
        if (Core.Validation.WpMarkers.IsAmbiguous(wp)) return " [yellow]⚠AMBIGUOUS[/]";
        return "";
    }

    private static string SpecHash(Workspace ws) =>
        File.Exists(ws.SpecPath)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(ws.SpecPath)))[..8].ToLowerInvariant()
            : "n/a";
}

public static class ControlCommands
{
    public static int Stop()
    {
        var ws = Workspace.Load();
        ExecutionControl.SetStop(ws.Root);
        ws.Audit.Append("STOP_REQUESTED");
        AnsiConsole.MarkupLine("[yellow]Stop requested.[/] The daemon halts at its next safe point; progress is preserved.");
        return 0;
    }

    public static int Pause()
    {
        var ws = Workspace.Load();
        ExecutionControl.SetPause(ws.Root);
        ws.Audit.Append("PAUSED_REQUESTED");
        AnsiConsole.MarkupLine("[yellow]Pause requested.[/] Run 'tenninety resume' then 'tenninety start' to continue.");
        return 0;
    }

    public static int Resume()
    {
        var ws = Workspace.Load();
        ExecutionControl.ClearAll(ws.Root);
        ws.States.Update(state =>
        {
            state.Paused = false;
            state.StopRequested = false;
        });
        ws.Audit.Append("RESUMED");
        AnsiConsole.MarkupLine("[green]Resumed.[/] Run 'tenninety start' to continue execution.");
        return 0;
    }
}
