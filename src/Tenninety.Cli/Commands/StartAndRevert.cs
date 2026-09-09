using Spectre.Console;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Security;
using Tenninety.Core.Stores;
using Tenninety.Execution;

namespace Tenninety.Cli.Commands;

/// <summary>tenninety start — autonomous serial execution of the queue (headless or TUI).</summary>
public static class StartCommand
{
    public static async Task<int> Run(bool headless)
    {
        DaemonLockLease? initialDaemonLock = DaemonLock.Acquire(Directory.GetCurrentDirectory());
        try
        {
            var ws = Workspace.Load();
            var pendingPromotion = new PromotionTransactionStore(
                Path.Combine(ws.Root, TenNinety.StateDir, TenNinety.PromotionFile)).Load();
            Plan plan;
            string? recoveryOnlyPlanError = null;
            try
            {
                plan = ws.LoadPlan();
            }
            catch (Exception ex) when (pendingPromotion?.Kind == TenNinety.PromotionKinds.Hotfix)
            {
                // Hotfix recovery is plan-independent. Do not strand durable evidence merely
                // because plan.json is absent or damaged; recover and leave planning for later.
                plan = new Plan { ProjectName = "hotfix recovery" };
                recoveryOnlyPlanError = Sanitizer.SanitizeDiagnostic(ex.Message, 1000);
            }
            var state = ws.States.Load();

            var orchestrator = new Orchestrator(
                ws.Git, plan, state, ws.Config, ws.CreateFrontier(),
                ws.States, ws.Audit, log: WriteLog,
                initialDaemonLock: initialDaemonLock);
            initialDaemonLock = null; // ownership transfers to the first RunAsync invocation

            var interactive = recoveryOnlyPlanError is null && !headless &&
                              !Console.IsInputRedirected && !Console.IsOutputRedirected;
            if (interactive)
                return await Tenninety.Tui.TuiHost.RunAsync(ws, plan, state, orchestrator);

            return await RunHeadless(orchestrator, recoveryOnlyPlanError);
        }
        finally
        {
            initialDaemonLock?.Dispose();
        }
    }

    private static async Task<int> RunHeadless(
        Orchestrator orchestrator, string? recoveryOnlyPlanError = null)
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
                OrchestratorExit.Completed when recoveryOnlyPlanError is not null => Succeeded(
                    "Hotfix recovery completed. plan.json remains unavailable: " +
                    recoveryOnlyPlanError),
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
            return Failed(1, Sanitizer.SanitizeDiagnostic(ex.Message, 4000));
        }

        static int Succeeded(string message) { AnsiConsole.MarkupLine($"[green]{Markup.Escape(message)}[/]"); return 0; }
        static int Failed(int code, string message) { AnsiConsole.MarkupLine($"[red]{Markup.Escape(message)}[/]"); return code; }
    }

    private static void WriteLog(string line) =>
        Console.WriteLine(Sanitizer.SanitizeDiagnostic($"[tenninety] {line}", 4000));
}

/// <summary>tenninety revert &lt;commit&gt; — hotfix flow (Part IV.5).</summary>
public static class RevertCommand
{
    public static async Task<int> Run(string commit, string? reason)
    {
        var ws = Workspace.Load();
        var tester = new AgentFactory(ws.Config).CreateTester(ws.Git, WriteLog);
        var service = new RevertService(ws.Git, ws.Config, ws.CreateFrontier(), tester, ws.Audit,
            log: WriteLog);

        var outcome = await service.RevertAsync(commit, reason ?? "", CancellationToken.None);
        if (outcome.Success)
        {
            AnsiConsole.MarkupLine(
                $"[green]{Markup.Escape(Sanitizer.SanitizeDiagnostic(outcome.Message, 4000))}[/]");
            return 0;
        }
        AnsiConsole.MarkupLine(
            $"[red]{Markup.Escape(Sanitizer.SanitizeDiagnostic(outcome.Message, 4000))}[/]");
        return 1;
    }

    private static void WriteLog(string line) =>
        Console.WriteLine(Sanitizer.SanitizeDiagnostic($"[tenninety] {line}", 4000));
}
