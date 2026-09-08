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
/// Enforces the blueprint planning rules on plan.json before it is accepted as the
/// execution graph, and treats the graph as UNTRUSTED MODEL OUTPUT (Part VI):
/// strict id format (blocks branch-name and shell injection downstream), closed layer set,
/// unique ids/dependencies, atomic decomposition, size caps, ambiguity-marker surfacing,
/// and complete null/shape hardening — a plan may arrive with null sections, null work
/// packages, null collections or null dictionary values, and every such deviation becomes a
/// controlled validation ERROR, never a NullReferenceException.
/// The blueprint prompt additionally requires every dependency to appear EARLIER in the
/// work_packages list than its dependent; that topological ordering is enforced mechanically
/// by comparing list indexes (in addition to the DAG and layer checks).
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

    // Untrusted-plan collection bounds: a hostile or runaway plan cannot blow up scheduling,
    // rendering or persistence even when every individual text is short enough.
    public const int MaxWorkPackages = 500;
    public const int MaxDependenciesPerPackage = 64;
    public const int MaxGlobalContextEntries = 100;
    public const int MaxDirectoryStructureEntries = 100;
    public const int MaxDirectoryStructureEntriesPerKey = 100;
    public const int MaxArchitectureEntries = 100;

    public static ValidationResult Validate(Plan plan)
    {
        var result = new ValidationResult();
        if (plan is null)
        {
            result.Errors.Add("plan must not be null.");
            return result;
        }

        if (!string.Equals(plan.SchemaVersion, TenNinety.SchemaVersion, StringComparison.Ordinal))
            result.Errors.Add($"schema_version must be '{TenNinety.SchemaVersion}' but was '{plan.SchemaVersion}'.");

        if (string.IsNullOrWhiteSpace(plan.ProjectName))
            result.Errors.Add("project_name must not be empty.");
        else if (plan.ProjectName.Length > 200)
            result.Errors.Add("project_name exceeds 200 characters.");

        if (plan.GlobalContext is null)
            result.Errors.Add("global_context must not be null.");
        else
            ValidateGlobalContext(plan.GlobalContext, result);

        ValidateArchitectureMap(plan.ArchitectureMap, result);

        ValidateWorkPackages(plan, result);

        return result;
    }

    private static void ValidateArchitectureMap(ArchitectureMap? map, ValidationResult result)
    {
        if (map is null) return; // the section itself is optional per the blueprint schema
        if (map.BoundedContexts is null)
            result.Errors.Add("architecture_map.bounded_contexts must not be null (use an empty list).");
        else if (map.BoundedContexts.Count > MaxArchitectureEntries)
            result.Errors.Add(
                $"architecture_map.bounded_contexts exceeds {MaxArchitectureEntries} entries.");
        if (map.CoreEntities is null)
            result.Errors.Add("architecture_map.core_entities must not be null (use an empty list).");
        else if (map.CoreEntities.Count > MaxArchitectureEntries)
            result.Errors.Add($"architecture_map.core_entities exceeds {MaxArchitectureEntries} entries.");
        if (map.KeyDependencies is null)
            result.Errors.Add("architecture_map.key_dependencies must not be null (use an empty list).");
        else if (map.KeyDependencies.Count > MaxArchitectureEntries)
            result.Errors.Add($"architecture_map.key_dependencies exceeds {MaxArchitectureEntries} entries.");
    }

    private static void ValidateGlobalContext(GlobalContext context, ValidationResult result)
    {
        if (context.CodingStandards is null)
            result.Errors.Add("global_context.coding_standards must not be null.");
        else if (context.CodingStandards.Count > MaxGlobalContextEntries)
            result.Errors.Add(
                $"global_context.coding_standards exceeds {MaxGlobalContextEntries} entries.");
        if (context.Assumptions is null)
            result.Errors.Add("global_context.assumptions must not be null.");
        else if (context.Assumptions.Count > MaxGlobalContextEntries)
            result.Errors.Add($"global_context.assumptions exceeds {MaxGlobalContextEntries} entries.");
        if (context.DirectoryStructure is null) return;
        if (context.DirectoryStructure.Count > MaxDirectoryStructureEntries)
        {
            result.Errors.Add(
                $"global_context.directory_structure exceeds {MaxDirectoryStructureEntries} entries.");
            return;
        }
        foreach (var (key, values) in context.DirectoryStructure)
        {
            if (key is null)
                result.Errors.Add("global_context.directory_structure contains a null key.");
            if (values is null)
                result.Errors.Add(
                    $"global_context.directory_structure['{key ?? "<null>"}'] must not " +
                    "be null (use an empty list).");
            else if (values.Count > MaxDirectoryStructureEntriesPerKey)
                result.Errors.Add(
                    $"global_context.directory_structure['{key}'] exceeds " +
                    $"{MaxDirectoryStructureEntriesPerKey} entries.");
        }
    }

    private static void ValidateWorkPackages(Plan plan, ValidationResult result)
    {
        if (plan.WorkPackages is null)
        {
            result.Errors.Add("work_packages must not be null (use an empty list or at least " +
                              "one work package).");
            return;
        }
        if (plan.WorkPackages.Contains(null!))
            result.Errors.Add("work_packages must not contain null entries.");
        if (plan.WorkPackages.Count == 0)
            result.Errors.Add("plan must contain at least one work package.");
        if (plan.WorkPackages.Count > MaxWorkPackages)
            result.Errors.Add(
                $"plan exceeds {MaxWorkPackages} work packages ({plan.WorkPackages.Count}); " +
                "split the specification instead.");

        // Null entries are reported above and skipped everywhere below; `ids`/`indexes` only
        // receive non-null entries so downstream DAG checks cannot fault either.
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var firstIndexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var indexed = plan.WorkPackages
            .Select((wp, index) => (wp, index))
            .Where(entry => entry.wp is not null)
            .ToList();

        foreach (var (wp, index) in indexed)
        {
            if (string.IsNullOrWhiteSpace(wp.Id))
            {
                result.Errors.Add($"work package #{index} has an empty id.");
                continue;
            }
            if (!ids.Add(wp.Id))
                result.Errors.Add($"duplicate work package id '{wp.Id}'.");
            else
                firstIndexById.TryAdd(wp.Id, index);
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

            ValidatePackageLists(wp, conflict: WpMarkers.IsConflict(wp), result);

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

            if (wp.Layer is null || !TenNinety.LayerRanks.ContainsKey(wp.Layer))
                result.Errors.Add(
                    $"'{wp.Id}': unknown layer '{wp.Layer ?? ""}' – expected one of " +
                    string.Join("/", TenNinety.LayerRanks.Keys));
        }

        var ordering = new List<string>();
        foreach (var (wp, index) in indexed)
        {
            if (wp.Dependencies is null)
            {
                result.Errors.Add($"'{wp.Id}': dependencies must not be null (use an empty list).");
                continue;
            }
            if (wp.Dependencies.Count > MaxDependenciesPerPackage)
                result.Errors.Add(
                    $"'{wp.Id}': more than {MaxDependenciesPerPackage} dependencies – split the package.");
            if (wp.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase).Count() != wp.Dependencies.Count)
                result.Errors.Add($"'{wp.Id}': duplicate dependency entries.");
            foreach (var dep in wp.Dependencies)
            {
                if (string.Equals(dep, wp.Id, StringComparison.OrdinalIgnoreCase))
                    result.Errors.Add($"'{wp.Id}': self-dependency.");
                else if (!ids.Contains(dep))
                    result.Errors.Add($"'{wp.Id}': dependency '{dep}' does not exist.");
                // Blueprint prompt rule: every dependency must appear EARLIER in the
                // work_packages list than its dependent. Enforced mechanically by index.
                // Tracked separately: an ordering violation does not make the graph itself
                // unanalyzable, so the DAG and layer checks still run for structurally
                // valid graphs and every deviation is reported.
                else if (firstIndexById.TryGetValue(dep, out var depIndex) && index < depIndex)
                    ordering.Add(
                        $"'{wp.Id}': dependency '{dep}' must appear EARLIER in the work_packages " +
                        "list than the package that depends on it (blueprint topological order).");
            }
        }

        if (result.Errors.Count == 0)
        {
            DetectCycles(plan, result);
            CheckLayerOrdering(plan, result);
        }
        result.Errors.AddRange(ordering);
    }

    /// <summary>Null-safe directive/acceptance-criteria handling: a null collection is an
    /// error, blank entries are removed with a warning (existing behavior), counts and text
    /// sizes are bounded.</summary>
    private static void ValidatePackageLists(WorkPackage wp, bool conflict, ValidationResult result)
    {
        if (wp.Directives is null)
            result.Errors.Add($"'{wp.Id}': directives must not be null (a CONFLICT package uses " +
                              "an empty list; any other package needs at least one directive).");
        if (wp.AcceptanceCriteria is null)
            result.Errors.Add($"'{wp.Id}': acceptance_criteria must not be null (use an empty list).");

        // Blueprint ambiguity protocol: a CONFLICT WP intentionally has no
        // directives ("do not generate directives"); it must never be scheduled. Any other WP
        // without directives violates atomic decomposition.
        var directives = (wp.Directives ?? []).Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
        if (wp.Directives is not null && directives.Count != wp.Directives.Count)
        {
            result.Warnings.Add($"'{wp.Id}': blank directives were removed.");
            wp.Directives = directives;
        }
        if (directives.Count == 0 && !conflict)
            result.Errors.Add($"'{wp.Id}': at least one directive is required (atomic decomposition).");

        var criteria = (wp.AcceptanceCriteria ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        if (wp.AcceptanceCriteria is not null && criteria.Count != wp.AcceptanceCriteria.Count)
        {
            result.Warnings.Add($"'{wp.Id}': blank acceptance criteria were removed.");
            wp.AcceptanceCriteria = criteria;
        }
        if (wp.Directives is not null && wp.AcceptanceCriteria is not null && criteria.Count == 0)
            result.Warnings.Add($"'{wp.Id}': no acceptance criteria defined.");

        if (directives.Count > MaxDirectives)
            result.Errors.Add($"'{wp.Id}': more than {MaxDirectives} directives – split the package.");
        if (directives.Any(d => d.Length > MaxDirectiveLength))
            result.Errors.Add($"'{wp.Id}': a directive exceeds {MaxDirectiveLength} characters.");
        if (criteria.Count > MaxCriteria)
            result.Errors.Add($"'{wp.Id}': more than {MaxCriteria} acceptance criteria – split the package.");
        if (criteria.Any(a => a.Length > MaxCriteriaLength))
            result.Errors.Add($"'{wp.Id}': an acceptance criterion exceeds {MaxCriteriaLength} characters.");
    }

    /// <summary>Kahn's algorithm; also returns a topological order usable for serial scheduling.
    /// Null-safe for untrusted plans (null dependency lists are treated as empty).</summary>
    public static List<string>? TopologicalOrder(Plan plan)
    {
        var byId = plan.WorkPackages.ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
        var indegree = plan.WorkPackages.ToDictionary(w => w.Id, _ => 0, StringComparer.OrdinalIgnoreCase);
        var dependents = plan.WorkPackages.ToDictionary(
            w => w.Id, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var wp in plan.WorkPackages)
            foreach (var dep in wp.Dependencies ?? [])
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
            foreach (var dep in wp.Dependencies ?? [])
            {
                if (!byId.TryGetValue(dep, out var depWp)) continue;
                if (TenNinety.LayerRanks.TryGetValue(depWp.Layer, out var depRank) && depRank > rank)
                {
                    // Blueprint rule 4: "A WP in a lower layer cannot depend on a
                    // WP in a higher layer." This is a hard error, not a style warning.
                    result.Errors.Add(
                        $"'{wp.Id}' ({wp.Layer}) depends on '{dep}' ({byId[dep].Layer}); " +
                        "a lower layer must never depend on a higher layer.");
                }
            }
        }
    }

    private static string TruncateNotes(string? notes)
    {
        notes = (notes ?? "").Trim();
        return notes.Length <= 120 ? $"\"{notes}\"" : $"\"{notes[..120]}…\"";
    }

    /// <summary>Natural ordering: WP-2 sorts before WP-10 regardless of zero padding.</summary>
    public static int IdOrder(string id)
    {
        var digits = new string(id.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }
}
