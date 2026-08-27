using System.Text.RegularExpressions;
using Tenninety.Core.Models;

namespace Tenninety.Core.Validation;

public sealed class ValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Enforces the v3.2 Enterprise planning rules on plan.json before it is accepted as the
/// execution graph, and treats the graph as UNTRUSTED MODEL OUTPUT (Part VI):
/// strict id format (blocks branch-name and shell injection downstream), closed layer set,
/// unique ids/dependencies, atomic decomposition, size caps, ambiguity-marker surfacing.
/// </summary>
public static partial class PlanValidator
{
    // Work-package ids: WP- followed by 3–6 digits (blueprint convention). Anything else is
    // rejected before the id can reach branch names, file paths or shell commands.
    [GeneratedRegex(@"^WP-[0-9]{3,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private const int MaxTitleLength = 300;
    private const int MaxGoalLength = 600;
    private const int MaxDirectiveLength = 500;
    private const int MaxCriteriaLength = 500;
    private const int MaxDirectives = 40;
    private const int MaxCriteria = 20;
    private const int MaxNotesLength = 4000;

    public static ValidationResult Validate(Plan plan)
    {
        var result = new ValidationResult();

        if (!string.Equals(plan.SchemaVersion, TenNinety.SchemaVersion, StringComparison.Ordinal))
            result.Errors.Add($"schema_version must be '{TenNinety.SchemaVersion}' but was '{plan.SchemaVersion}'.");

        if (string.IsNullOrWhiteSpace(plan.ProjectName))
            result.Errors.Add("project_name must not be empty.");
        if (plan.ProjectName.Length > 200)
            result.Errors.Add("project_name exceeds 200 characters.");

        if (plan.WorkPackages.Count == 0)
            result.Errors.Add("plan must contain at least one work package.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wp in plan.WorkPackages)
        {
            if (string.IsNullOrWhiteSpace(wp.Id))
            {
                result.Errors.Add($"work package #{plan.WorkPackages.IndexOf(wp)} has an empty id.");
                continue;
            }
            if (!ids.Add(wp.Id))
                result.Errors.Add($"duplicate work package id '{wp.Id}'.");
            if (!IdPattern().IsMatch(wp.Id))
                result.Errors.Add(
                    $"'{wp.Id}': id must match WP- plus 3–6 digits (e.g. WP-001) – " +
                    "ids become git branch names and must stay machine-safe.");
            if (string.IsNullOrWhiteSpace(wp.Title))
                result.Errors.Add($"'{wp.Id}': title must not be empty.");
            else if (wp.Title.Length > MaxTitleLength)
                result.Errors.Add($"'{wp.Id}': title exceeds {MaxTitleLength} characters.");
            if (string.IsNullOrWhiteSpace(wp.Goal))
                result.Errors.Add($"'{wp.Id}': goal must not be empty.");
            else if (wp.Goal.Length > MaxGoalLength)
                result.Errors.Add($"'{wp.Id}': goal exceeds {MaxGoalLength} characters.");

            // Blueprint v3.2 Enterprise ambiguity protocol: a CONFLICT WP intentionally has no
            // directives ("do not generate directives"); it must never be scheduled. Any other WP
            // without directives violates atomic decomposition.
            var conflict = WpMarkers.IsConflict(wp);
            var directives = wp.Directives.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
            if (directives.Count != wp.Directives.Count)
            {
                result.Warnings.Add($"'{wp.Id}': blank directives were removed.");
                wp.Directives = directives;
            }
            if (wp.Directives.Count == 0 && !conflict)
                result.Errors.Add($"'{wp.Id}': at least one directive is required (atomic decomposition).");

            var criteria = wp.AcceptanceCriteria.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            if (criteria.Count != wp.AcceptanceCriteria.Count)
            {
                result.Warnings.Add($"'{wp.Id}': blank acceptance criteria were removed.");
                wp.AcceptanceCriteria = criteria;
            }
            if (wp.AcceptanceCriteria.Count == 0)
                result.Warnings.Add($"'{wp.Id}': no acceptance criteria defined.");

            if (wp.Directives.Count > MaxDirectives)
                result.Errors.Add($"'{wp.Id}': more than {MaxDirectives} directives – split the package.");
            if (wp.Directives.Any(d => d.Length > MaxDirectiveLength))
                result.Errors.Add($"'{wp.Id}': a directive exceeds {MaxDirectiveLength} characters.");
            if (wp.AcceptanceCriteria.Count > MaxCriteria)
                result.Errors.Add($"'{wp.Id}': more than {MaxCriteria} acceptance criteria – split the package.");
            if (wp.AcceptanceCriteria.Any(a => a.Length > MaxCriteriaLength))
                result.Errors.Add($"'{wp.Id}': an acceptance criterion exceeds {MaxCriteriaLength} characters.");

            if (!string.IsNullOrWhiteSpace(wp.Notes) && wp.Notes.Length > MaxNotesLength)
                result.Errors.Add($"'{wp.Id}': notes exceed {MaxNotesLength} characters.");

            if (string.IsNullOrWhiteSpace(wp.Module))
                result.Warnings.Add($"'{wp.Id}': no module/bounded context assigned.");
            foreach (var marker in WpMarkers.MarkersOf(wp))
                result.Warnings.Add($"'{wp.Id}' carries {marker}: {TruncateNotes(wp.Notes)}");

            // Untrusted input: arriving plans must declare PENDING. The parsing boundary
            // (HttpFrontierClient) enforces the reset; here we only REPORT deviations so
            // re-validation of already-applied states (e.g. pivot CANCELLED) stays pure.
            var knownStatus = wp.Status is TenNinety.WpStatus.Pending
                or TenNinety.WpStatus.Active
                or TenNinety.WpStatus.Done
                or TenNinety.WpStatus.Blocked
                or TenNinety.WpStatus.Cancelled;
            if (!knownStatus)
                result.Errors.Add($"'{wp.Id}': unknown or non-canonical status '{wp.Status}'.");
            else if (wp.Status != TenNinety.WpStatus.Pending)
            {
                result.Warnings.Add(
                    $"'{wp.Id}': arriving plans must use status '{TenNinety.WpStatus.Pending}' " +
                    $"(found '{wp.Status}'; the planner boundary resets this automatically).");
            }

            if (!TenNinety.LayerRanks.ContainsKey(wp.Layer))
                result.Errors.Add(
                    $"'{wp.Id}': unknown layer '{wp.Layer}' – expected one of " +
                    string.Join("/", TenNinety.LayerRanks.Keys));
        }

        foreach (var wp in plan.WorkPackages)
        {
            if (wp.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != wp.Dependencies.Count)
                result.Errors.Add($"'{wp.Id}': duplicate dependency entries.");
            foreach (var dep in wp.Dependencies)
            {
                if (string.Equals(dep, wp.Id, StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"'{wp.Id}': self-dependency.");
                else if (!ids.Contains(dep))
                    result.Errors.Add($"'{wp.Id}': dependency '{dep}' does not exist.");
            }
        }

        if (result.Errors.Count == 0)
        {
            DetectCycles(plan, result);
            CheckLayerOrdering(plan, result);
        }

        return result;
    }

    /// <summary>Kahn's algorithm; also returns a topological order usable for serial scheduling.</summary>
    public static List<string>? TopologicalOrder(Plan plan)
    {
        var byId = plan.WorkPackages.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        var indegree = plan.WorkPackages.ToDictionary(w => w.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var dependents = plan.WorkPackages.ToDictionary(
            w => w.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var wp in plan.WorkPackages)
            foreach (var dep in wp.Dependencies)
            {
                if (!byId.ContainsKey(dep)) return null;
                indegree[wp.Id]++;
                dependents[dep].Add(wp.Id);
            }

        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key).OrderBy(IdOrder));
        var order = new List<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var next in dependents[id])
                if (--indegree[next] == 0)
                    queue.Enqueue(next);
        }

        return order.Count == plan.WorkPackages.Count ? order : null;
    }

    private static void DetectCycles(Plan plan, ValidationResult result)
    {
        if (TopologicalOrder(plan) is null)
            result.Errors.Add("dependency graph contains a cycle (strict DAG rule violated).");
    }

    private static void CheckLayerOrdering(Plan plan, ValidationResult result)
    {
        var byId = plan.WorkPackages.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var wp in plan.WorkPackages)
        {
            if (!TenNinety.LayerRanks.TryGetValue(wp.Layer, out var rank)) continue;
            foreach (var dep in wp.Dependencies)
            {
                if (!byId.TryGetValue(dep, out var depWp)) continue;
                if (TenNinety.LayerRanks.TryGetValue(depWp.Layer, out var depRank) && depRank > rank)
                {
                    // Blueprint v3.2 Enterprise rule 4: "A WP in a lower layer cannot depend on a
                    // WP in a higher layer." This is a hard error, not a style warning.
                    result.Errors.Add(
                        $"'{wp.Id}' ({wp.Layer}) depends on '{dep}' ({byId[dep].Layer}); " +
                        "a lower layer must never depend on a higher layer.");
                }
            }
        }
    }

    private static string TruncateNotes(string notes)
    {
        notes = notes.Trim();
        return notes.Length <= 120 ? $"\"{notes}\"" : $"\"{notes[..120]}…\"";
    }

    /// <summary>Natural ordering: WP-2 sorts before WP-10 regardless of zero padding.</summary>
    public static int IdOrder(string id)
    {
        var digits = new string(id.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }
}
