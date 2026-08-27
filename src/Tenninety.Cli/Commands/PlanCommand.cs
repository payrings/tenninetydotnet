using Spectre.Console;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Security;
using Tenninety.Core.Validation;
using Tenninety.Execution;

namespace Tenninety.Cli.Commands;

/// <summary>tenninety plan — Frontier decomposition of spec.md into plan.json (Phase 2).</summary>
public static class PlanCommand
{
    public static async Task<int> Run(string? specArg, bool assumeYes)
    {
        var ws = Workspace.Load();
        if (ws.States.Exists())
        {
            var existing = ws.States.Load();
            if (HasExecutionProgress(existing))
            {
                AnsiConsole.MarkupLine(
                    "[red]An execution already has progress.[/] Use Snapshot & Pivot instead of replacing its plan.");
                return 1;
            }
        }

        var specPath = specArg is not null ? Path.GetFullPath(specArg) : ws.SpecPath;
        if (!File.Exists(specPath))
        {
            AnsiConsole.MarkupLine($"[red]spec not found:[/] {Markup.Escape(specPath)}");
            return 1;
        }

        // Part VI.1: spec content is untrusted data; sanitize before it reaches any model.
        var rawSpec = File.ReadAllText(specPath);
        var sanitized = Sanitizer.SanitizeText(rawSpec);
        var specHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawSpec)))[..8].ToLowerInvariant();

        var frontier = ws.CreateFrontier();
        AnsiConsole.MarkupLine(
            $"[dim]Sending spec ({sanitized.Length} chars, sha {specHash}) to frontier " +
            $"({Markup.Escape(ws.Config.ProviderMode)})…[/]");

        Plan plan;
        try
        {
            plan = await frontier.GeneratePlanAsync(sanitized);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AnsiConsole.MarkupLine($"[red]frontier planning failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        var validation = PlanValidator.Validate(plan);
        RenderPlanSummary(plan, validation);
        if (!validation.IsValid)
        {
            AnsiConsole.MarkupLine("[red]plan rejected — fix the spec or retry with a stronger frontier model.[/]");
            return 1;
        }
        foreach (var warning in validation.Warnings)
            AnsiConsole.MarkupLine($"[yellow]warning:[/] {Markup.Escape(warning)}");

        // Blueprint v3.2 Enterprise: CONFLICT WPs will never be executed until a pivot REWORKs them.
        var conflicts = plan.WorkPackages.Where(WpMarkers.IsConflict).ToList();
        var ambiguities = plan.WorkPackages.Where(w => WpMarkers.IsAmbiguous(w) && !WpMarkers.IsConflict(w)).ToList();
        if (conflicts.Count > 0)
            AnsiConsole.MarkupLine(
                $"[red]{conflicts.Count} CONFLICT work package(s) ({Markup.Escape(string.Join(", ", conflicts.Select(w => w.Id)))}) " +
                "will be excluded from execution until resolved via a pivot.[/]");
        if (ambiguities.Count > 0)
            AnsiConsole.MarkupLine(
                $"[yellow]{ambiguities.Count} AMBIGUOUS work package(s) ({Markup.Escape(string.Join(", ", ambiguities.Select(w => w.Id)))}) " +
                "carry assumptions — review their notes before accepting.[/]");

        var defaultAnswer = conflicts.Count == 0 && ambiguities.Count == 0;
        if (!assumeYes && !AnsiConsole.Confirm("Accept this execution graph and write plan.json?", defaultAnswer))
        {
            AnsiConsole.MarkupLine("[yellow]Discarded.[/]");
            return 0;
        }

        IDisposable workspaceLock;
        try
        {
            workspaceLock = DaemonLock.Acquire(ws.Root);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]cannot accept plan:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        using (workspaceLock)
        {
            if (ws.Git.CurrentBranch() != TenNinety.MainBranch)
            {
                AnsiConsole.MarkupLine("[red]cannot accept plan:[/] workspace must be on main.");
                return 1;
            }
            var runtimeIgnore = $"{TenNinety.StateDir}/.gitignore";
            if (!ws.Git.IsPathClean(runtimeIgnore))
            {
                AnsiConsole.MarkupLine(
                    $"[red]cannot accept plan:[/] {Markup.Escape(runtimeIgnore)} has uncommitted edits.");
                return 1;
            }
            if (RuntimeGitignoreMigration.Ensure(ws.Root))
                ws.Git.CommitPaths([runtimeIgnore], "tenninety: update runtime ignores");
            if (!File.Exists(specPath) || File.ReadAllText(specPath) != rawSpec)
            {
                AnsiConsole.MarkupLine("[red]cannot accept plan:[/] the source spec changed while planning; retry.");
                return 1;
            }
            if (ws.States.Exists() && HasExecutionProgress(ws.States.Load()))
            {
                AnsiConsole.MarkupLine(
                    "[red]cannot accept plan:[/] execution progressed while planning; use Snapshot & Pivot.");
                return 1;
            }

            // spec.md is the tracked source of truth. An alternate --spec path is accepted as
            // an import, then copied to the canonical workspace path before persistence.
            if (!Path.GetFullPath(specPath).Equals(Path.GetFullPath(ws.SpecPath), StringComparison.Ordinal))
                File.WriteAllText(ws.SpecPath, rawSpec);
            ws.Plans.Save(plan);

            var state = new RuntimeState
            {
                ExecutionMode = ws.Config.ExecutionMode,
                SpecHash = specHash,
            };
            foreach (var wp in plan.WorkPackages)
                state.QueueStatus[wp.Id] = wp.Status == TenNinety.WpStatus.Pending
                    ? TenNinety.WpStatus.Pending
                    : wp.Status;
            ws.States.Save(state);

            ws.Git.CommitPaths(
                [TenNinety.SpecFile, $"{TenNinety.StateDir}/{TenNinety.PlanFile}"],
                $"plan: accept execution graph for '{plan.ProjectName}' ({plan.WorkPackages.Count} WPs)");
            ws.Audit.Append("PLAN_GENERATED", detail: $"project={plan.ProjectName} wps={plan.WorkPackages.Count} spec={specHash}");
        }

        AnsiConsole.MarkupLine(
            $"[green]Wrote .tenninety/{TenNinety.PlanFile}.[/] Review with 'tenninety status', then run 'tenninety start'.");
        return 0;
    }

    private static bool HasExecutionProgress(RuntimeState state) =>
        state.CurrentWp is not null ||
        state.Attempts.Count > 0 ||
        state.QueueStatus.Values.Any(status => status != TenNinety.WpStatus.Pending);

    private static void RenderPlanSummary(Plan plan, Core.Validation.ValidationResult validation)
    {
        AnsiConsole.Write(new Rule($"[b]{Markup.Escape(plan.ProjectName)}[/]").RuleStyle("grey"));
        var byLayer = plan.WorkPackages.GroupBy(w => w.Layer).OrderBy(g => g.Key);
        foreach (var group in byLayer)
            AnsiConsole.MarkupLine($"[b]{Markup.Escape(group.Key)}[/]: {group.Count()} WP(s)");

        // Blueprint v3.2 Enterprise structural analysis.
        var map = plan.ArchitectureMap;
        if (map is not null)
        {
            if (map.BoundedContexts.Count > 0)
                AnsiConsole.MarkupLine($"[b]Bounded contexts[/]: {Markup.Escape(string.Join(", ", map.BoundedContexts))}");
            if (map.CoreEntities.Count > 0)
                AnsiConsole.MarkupLine($"[b]Core entities[/]: {Markup.Escape(string.Join(", ", map.CoreEntities))}");
            foreach (var dep in map.KeyDependencies)
                AnsiConsole.MarkupLine($"[b]Key dependency[/]: {Markup.Escape(dep)}");
        }
        if (plan.GlobalContext.DirectoryStructure is { } dirs && dirs.Count > 0)
            foreach (var (root, projects) in dirs)
                AnsiConsole.MarkupLine(
                    $"[b]{Markup.Escape(root)}[/]: {Markup.Escape(string.Join(", ", projects))}");
        if (plan.GlobalContext.Assumptions.Count > 0)
            AnsiConsole.MarkupLine(
                $"[b]Assumptions[/]: {plan.GlobalContext.Assumptions.Count} recorded");

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumns("#", "ID", "Layer", "Module", "Title", "Deps", "Directives", "Criteria", "Notes");
        foreach (var (wp, i) in plan.WorkPackages.OrderBy(w => PlanValidator.IdOrder(w.Id)).Select((w, i) => (w, i)))
        {
            var notes = "";
            if (WpMarkers.IsConflict(wp)) notes = "[red]CONFLICT[/]";
            else if (WpMarkers.IsAmbiguous(wp)) notes = "[yellow]AMBIGUOUS[/]";
            table.AddRow(
                (i + 1).ToString(),
                Markup.Escape(wp.Id),
                Markup.Escape(wp.Layer),
                Markup.Escape(wp.Module),
                Markup.Escape(wp.Title),
                wp.Dependencies.Count == 0 ? "-" : Markup.Escape(string.Join(",", wp.Dependencies)),
                wp.Directives.Count.ToString(),
                wp.AcceptanceCriteria.Count.ToString(),
                notes);
        }
        AnsiConsole.Write(table);
    }
}
