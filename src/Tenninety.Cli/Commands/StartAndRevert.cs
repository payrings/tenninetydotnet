using Spectre.Console;
using Tenninety.Core;
using Tenninety.Execution;

namespace Tenninety.Cli.Commands;

/// <summary>tenninety start — autonomous serial execution of the queue (headless or TUI).</summary>
public static class StartCommand
{
    public static async Task<int> Run(bool headless)
    {
        var ws = Workspace.Load();
        var plan = ws.LoadPlan();
        var state = ws.States.Load();

        var orchestrator = new Orchestrator(
            ws.Git, plan, state, ws.Config, ws.CreateFrontier(),
            ws.States, ws.Audit, log: line => Console.WriteLine($"[tenninety] {line}"));

        var interactive = !headless && !Console.IsInputRedirected && !Console.IsOutputRedirected;
        if (interactive)
            return await Tenninety.Tui.TuiHost.RunAsync(ws, plan, state, orchestrator);

        return await RunHeadless(orchestrator);
    }

    private static async Task<int> RunHeadless(Orchestrator orchestrator)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // graceful first Ctrl+C: request a safe stop
            orchestrator.RequestStop();
            cts.CancelAfter(TimeSpan.FromSeconds(10));
        };

        AnsiConsole.MarkupLine("[dim]Serial execution started (Ctrl+C for a graceful stop).[/]");
        try
        {
            var exit = await orchestrator.RunAsync(cts.Token);
            return exit switch
            {
                OrchestratorExit.Completed => Succeeded("All work packages are DONE."),
                OrchestratorExit.Paused => Succeeded("Daemon paused — run 'tenninety resume' then 'tenninety start'."),
                OrchestratorExit.Stopped => Succeeded("Daemon stopped — progress saved."),
                OrchestratorExit.Deadlocked => Failed(4, "Queue deadlocked (BLOCKED WPs block their dependents)."),
                _ => Failed(1, $"unexpected exit: {exit}"),
            };
        }
        catch (OperationCanceledException)
        {
            return Succeeded("Cancelled — progress saved.");
        }
        catch (Exception ex)
        {
            return Failed(1, ex.Message);
        }

        static int Succeeded(string message) { AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]"); return 0; }
        static int Failed(int code, string message) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]"); return code; }
    }
}

/// <summary>tenninety revert &lt;commit&gt; — hotfix flow (Part IV.5).</summary>
public static class RevertCommand
{
    public static async Task<int> Run(string commit, string? reason)
    {
        var ws = Workspace.Load();
        var tester = new AgentFactory(ws.Config).CreateTester(line => Console.WriteLine($"[tenninety] {line}"));
        var service = new RevertService(ws.Git, ws.Config, ws.CreateFrontier(), tester, ws.Audit,
            log: line => Console.WriteLine($"[tenninety] {line}"));

        var outcome = await service.RevertAsync(commit, reason ?? "", CancellationToken.None);
        if (outcome.Success)
        {
            AnsiConsole.MarkupLine($"[green]{Markup.Escape(outcome.Message)}[/]");
            return 0;
        }
        AnsiConsole.MarkupLine($"[red]{Markup.Escape(outcome.Message)}[/]");
        return 1;
    }
}
