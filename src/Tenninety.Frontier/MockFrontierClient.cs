using System.Text;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;

namespace Tenninety.Frontier;

/// <summary>
/// Deterministic offline Frontier stand-in. Powers Phase-1 simulation ("can execute the queue without
/// models") and keeps the whole pipeline testable without network access.
/// </summary>
public sealed class MockFrontierClient : IFrontierClient
{
    public Task<Plan> GeneratePlanAsync(string sanitizedSpecMarkdown, CancellationToken ct = default)
    {
        var projectName = ExtractProjectName(sanitizedSpecMarkdown);
        var stack = ExtractTechStack(sanitizedSpecMarkdown);

        var plan = new Plan
        {
            ProjectName = projectName,
            GlobalContext = new GlobalContext
            {
                TechStack = stack,
                CodingStandards = new List<string> { "Follow spec coding standards", "Prefer async I/O" },
                Assumptions = new List<string> { "Mock planner: derived heuristically from spec.md" },
                DirectoryStructure = new Dictionary<string, List<string>>
                {
                    ["src"] = new List<string> { "Core", "Infrastructure", "Api" },
                    ["tests"] = new List<string> { "UnitTests", "IntegrationTests" },
                },
            },
            ArchitectureMap = new ArchitectureMap
            {
                BoundedContexts = new List<string> { "Core" },
                CoreEntities = ExtractEntityNames(sanitizedSpecMarkdown),
                KeyDependencies = new List<string>(),
            },
            WorkPackages = new List<WorkPackage>
            {
                new()
                {
                    Id = "WP-001", Layer = "INFRA", Module = "Core", Title = "Scaffold project & infrastructure",
                    Goal = $"Initialize the {projectName} codebase, build tooling and configuration.",
                    Directives = new List<string>
                    {
                        "Create solution/project skeleton.",
                        "Add configuration loading and logging bootstrap.",
                    },
                    AcceptanceCriteria = new List<string> { "Project builds successfully." },
                },
                new()
                {
                    Id = "WP-002", Layer = "DOMAIN", Module = "Core", Title = "Implement core domain entities",
                    Dependencies = new List<string> { "WP-001" },
                    Goal = "Model the core business entities described in the spec.",
                    Directives = new List<string>
                    {
                        "Create entities for primary domain concepts.",
                        "Enforce invariants stated in the spec.",
                    },
                    AcceptanceCriteria = new List<string> { "Entities compile.", "Unit tests cover invariants." },
                },
                new()
                {
                    Id = "WP-003", Layer = "DATA", Module = "Core", Title = "Implement persistence layer",
                    Dependencies = new List<string> { "WP-002" },
                    Goal = "Persist domain entities per the technical hints in the spec.",
                    Directives = new List<string> { "Create repository abstractions and implementations." },
                    AcceptanceCriteria = new List<string> { "Repositories round-trip entities." },
                },
                new()
                {
                    Id = "WP-004", Layer = "APP", Module = "Core", Title = "Implement application services",
                    Dependencies = new List<string> { "WP-003" },
                    Goal = "Implement the workflows described in the business rules.",
                    Directives = new List<string> { "Create services orchestrating repositories." },
                    AcceptanceCriteria = new List<string> { "Service unit tests pass." },
                },
                new()
                {
                    Id = "WP-101", Layer = "TEST-INTEGRATION", Module = "Integration",
                    Title = "Cross-module integration suite",
                    Dependencies = new List<string> { "WP-004" },
                    Goal = "Validate end-to-end behavior across layers.",
                    Directives = new List<string> { "Write integration tests covering main workflows." },
                    AcceptanceCriteria = new List<string> { "Integration suite passes." },
                },
            },
        };

        foreach (var wp in plan.WorkPackages)
            wp.Status = TenNinety.WpStatus.Pending;

        return Task.FromResult(plan);
    }

    public Task<RepairAdvice> GetRepairAdviceAsync(RepairRequest request, CancellationToken ct = default) =>
        Task.FromResult(new RepairAdvice
        {
            Analysis =
                $"Local executor stalled on '{request.WorkPackage.Id}' after {request.TotalAttempts} attempts. "
                + "Recurring failure themes detected in feedback.",
            Advice = new List<string>
            {
                $"Re-read directive list of '{request.WorkPackage.Id}' literally; implement each item as its own commit-scoped change.",
                request.Feedback.Count > 0
                    ? $"Address the most frequent feedback first: '{request.Feedback[0]}'."
                    : "Produce at least one file change per attempt so progress is observable.",
                "Verify every acceptance criterion manually before finishing the attempt.",
            },
        });

    public Task<PivotProposal> ProposePivotAsync(PivotRequest request, CancellationToken ct = default)
    {
        // Heuristic mock: REWORK packages whose text overlaps intent keywords; KEEP the rest.
        var keywords = request.UserIntent
            .Split([' ', ',', '.', ';', ':', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        var plan = Json.Deserialize<Plan>(request.PlanJson);
        var proposal = new PivotProposal { Rationale = "Mock pivot analysis based on keyword overlap with intent." };
        foreach (var wp in plan.WorkPackages)
        {
            if (wp.IsTerminal && wp.Status == TenNinety.WpStatus.Done &&
                !Matches(wp, keywords))
            {
                proposal.Keep.Add(wp.Id);
            }
            else if (Matches(wp, keywords))
            {
                proposal.Rework.Add(new PivotRework
                {
                    Id = wp.Id,
                    Reason = "Package text intersects pivot intent.",
                    UpdatedDirectives = new List<string> { $"Align '{wp.Title}' with new direction: {request.UserIntent}" },
                });
            }
            else
            {
                proposal.Keep.Add(wp.Id);
            }
        }
        return Task.FromResult(proposal);
    }

    public Task<RevertGuidance> ProposeRevertAsync(RevertRequest request, CancellationToken ct = default) =>
        Task.FromResult(new RevertGuidance
        {
            Analysis = "Mechanical revert is expected to be sufficient; validate with the test suite afterwards.",
            MechanicalRevertSufficient = true,
            Steps = new List<string>
            {
                "Create hotfix branch from main.",
                "git revert --no-edit <commit>",
                "Run mechanical tests; merge only on PASS.",
            },
        });

    private static bool Matches(WorkPackage wp, HashSet<string> keywords)
    {
        var haystack = string.Join(' ', new[] { wp.Title, wp.Goal }.Concat(wp.Directives).Concat(wp.AcceptanceCriteria));
        return haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(t => keywords.Contains(t.Trim().ToLowerInvariant()));
    }

    private static string ExtractProjectName(string spec)
    {
        foreach (var line in spec.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('#') && t.Length > 2)
            {
                var heading = t.TrimStart('#', ' ').Trim();
                if (!heading.Equals("specification", StringComparison.OrdinalIgnoreCase) && heading.Length <= 60)
                    return heading;
            }
        }
        return "SpecProject";
    }

    private static string ExtractTechStack(string spec)
    {
        var sb = new StringBuilder();
        foreach (var keyword in new[] { ".NET", "C#", "PostgreSQL", "Blazor", "React", "Node", "Python", "SQLite" })
            if (spec.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                sb.Append(sb.Length == 0 ? "" : ", ").Append(keyword);
        return sb.Length > 0 ? sb.ToString() : "(unspecified)";
    }

    /// <summary>Crude entity extraction: capitalized nouns from the spec's first section headings.</summary>
    private static List<string> ExtractEntityNames(string spec)
    {
        var candidates = spec
            .Split([' ', '\n', ',', '.', ':', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length is >= 4 and <= 24)
            .Where(t => char.IsUpper(t[0]) && t.All(char.IsLetter))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(t => !new[] { "Business", "Technical", "Rules", "Hints", "Descriptions", "The", "This" }
                .Contains(t, StringComparer.OrdinalIgnoreCase))
            .Take(5)
            .ToList();
        return candidates;
    }
}
