using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;
using Tenninety.Core.Validation;

namespace Tenninety.Tests;

/// <summary>
/// Plans are UNTRUSTED MODEL OUTPUT: null sections, null packages, null collections, null
/// dictionary values, unknown/duplicate members and excessive counts must all become
/// controlled validation errors — never a NullReferenceException — and the blueprint
/// "every dependency appears EARLIER in the list" rule is enforced mechanically by index.
/// </summary>
public sealed class PlanValidationHardeningTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly PlanStore _store;

    public PlanValidationHardeningTests() => _store = new PlanStore(_tmp.Path("plan.json"));

    public void Dispose() => _tmp.Dispose();

    private static Plan ValidPlan()
    {
        var plan = new Plan { ProjectName = "Demo" };
        plan.WorkPackages.Add(new WorkPackage
        {
            Id = "WP-001",
            Layer = "INFRA",
            Title = "scaffold",
            Goal = "scaffold the project",
            Directives = { "create the solution" },
            AcceptanceCriteria = { "solution builds" },
        });
        plan.WorkPackages.Add(new WorkPackage
        {
            Id = "WP-002",
            Layer = "DOMAIN",
            Title = "entities",
            Goal = "add entities",
            Dependencies = { "WP-001" },
            Directives = { "add the entity" },
            AcceptanceCriteria = { "entity compiles" },
        });
        return plan;
    }

    // ---- null hardening ------------------------------------------------------------------

    [Fact]
    public void Null_global_context_is_an_error_not_a_fault()
    {
        var plan = ValidPlan();
        plan.GlobalContext = null!;

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("global_context must not be null"));
    }

    [Fact]
    public void Null_work_packages_is_an_error_not_a_fault()
    {
        var plan = ValidPlan();
        plan.WorkPackages = null!;

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("work_packages must not be null"));
    }

    [Fact]
    public void Null_work_package_entries_are_an_error_not_a_fault()
    {
        var plan = ValidPlan();
        plan.WorkPackages.Add(null!);

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("null entries"));
    }

    [Fact]
    public void Null_dependencies_directives_and_criteria_are_errors_not_faults()
    {
        var plan = ValidPlan();
        plan.WorkPackages[0].Dependencies = null!;
        plan.WorkPackages[0].Directives = null!;
        plan.WorkPackages[0].AcceptanceCriteria = null!;

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("'WP-001': dependencies must not be null"));
        Assert.Contains(result.Errors, e => e.Contains("'WP-001': directives must not be null"));
        Assert.Contains(result.Errors, e =>
            e.Contains("'WP-001': acceptance_criteria must not be null"));
    }

    [Fact]
    public void Null_architecture_map_collections_are_errors_not_faults()
    {
        var plan = ValidPlan();
        plan.ArchitectureMap = new ArchitectureMap
        {
            BoundedContexts = null!,
            CoreEntities = null!,
            KeyDependencies = null!,
        };

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("architecture_map.bounded_contexts"));
        Assert.Contains(result.Errors, e => e.Contains("architecture_map.core_entities"));
        Assert.Contains(result.Errors, e => e.Contains("architecture_map.key_dependencies"));
    }

    [Fact]
    public void Null_global_context_collections_are_errors_not_faults()
    {
        var plan = ValidPlan();
        plan.GlobalContext.CodingStandards = null!;
        plan.GlobalContext.Assumptions = null!;

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("coding_standards must not be null"));
        Assert.Contains(result.Errors, e => e.Contains("assumptions must not be null"));
    }

    [Fact]
    public void Null_directory_structure_values_are_errors_not_faults()
    {
        var plan = ValidPlan();
        plan.GlobalContext.DirectoryStructure = new Dictionary<string, List<string>>
        {
            ["src"] = null!,
        };

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("directory_structure['src']"));
    }

    [Fact]
    public void A_null_plan_instance_is_reported()
    {
        var result = PlanValidator.Validate(null!);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("plan must not be null"));
    }

    // ---- strict ingestion: unknown/duplicate fields, size and depth -------------------------

    [Fact]
    public void Unknown_plan_members_are_rejected_on_load()
    {
        File.WriteAllText(_tmp.Path("plan.json"),
            """{ "schema_version": "1", "project_name": "X", "wrok_packages": [] }""");

        var ex = Assert.Throws<InvalidOperationException>(_store.Load);

        Assert.Contains("wrok_packages", ex.Message);
    }

    [Fact]
    public void Duplicate_plan_members_are_rejected_on_load()
    {
        File.WriteAllText(_tmp.Path("plan.json"),
            """{ "schema_version": "1", "project_name": "X", "project_name": "Y", "work_packages": [] }""");

        var ex = Assert.Throws<InvalidOperationException>(_store.Load);

        Assert.Contains("duplicate field 'project_name'", ex.Message);
    }

    [Fact]
    public void Duplicate_members_inside_a_work_package_are_rejected_on_load()
    {
        File.WriteAllText(_tmp.Path("plan.json"),
            """
            {
              "schema_version": "1",
              "project_name": "X",
              "work_packages": [
                { "id": "WP-001", "id": "WP-002", "layer": "INFRA", "title": "t", "goal": "g",
                  "directives": ["d"], "acceptance_criteria": [], "dependencies": [], "notes": "", "status": "PENDING" }
              ]
            }
            """);

        var ex = Assert.Throws<InvalidOperationException>(_store.Load);

        Assert.Contains("duplicate field 'id'", ex.Message);
    }

    [Fact]
    public void An_oversized_persisted_plan_is_rejected_by_the_size_bound()
    {
        var padding = new string('x', checked((int)StrictJsonIngestion.MaxPlanBytes + 16));
        File.WriteAllText(_tmp.Path("plan.json"), "{ \"notes_pad\": \"" + padding + "\" }");

        var ex = Assert.Throws<InvalidOperationException>(_store.Load);

        Assert.Contains("size bound", ex.Message);
    }

    [Fact]
    public void Excessive_plan_depth_is_rejected_on_load()
    {
        var depth = StrictJsonIngestion.MaxDepth + 3;
        File.WriteAllText(_tmp.Path("plan.json"), new string('[', depth) + new string(']', depth));

        var ex = Assert.Throws<InvalidOperationException>(_store.Load);

        Assert.Contains("depth", ex.Message);
    }

    // ---- the blueprint topological-order rule -------------------------------------------------

    [Fact]
    public void A_dependency_listed_after_its_dependent_is_an_error()
    {
        var plan = ValidPlan();
        // Reverse the order: WP-002 depends on WP-001 but is declared first.
        plan.WorkPackages.Reverse();

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains("'WP-002'") &&
            e.Contains("dependency 'WP-001' must appear EARLIER"));
    }

    [Fact]
    public void A_plan_in_dependency_order_passes_the_ordering_rule()
    {
        var result = PlanValidator.Validate(ValidPlan());
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void The_ordering_rule_reports_every_forward_reference()
    {
        var plan = ValidPlan();
        plan.WorkPackages.Add(new WorkPackage
        {
            Id = "WP-003",
            Layer = "APP",
            Title = "service",
            Goal = "service layer",
            Dependencies = { "WP-004", "WP-005" },
            Directives = { "add the service" },
        });
        plan.WorkPackages.Add(new WorkPackage
        {
            Id = "WP-004",
            Layer = "DOMAIN",
            Title = "value object",
            Goal = "value object",
            Directives = { "add the value object" },
        });
        plan.WorkPackages.Add(new WorkPackage
        {
            Id = "WP-005",
            Layer = "DATA",
            Title = "repository",
            Goal = "repository",
            Directives = { "add the repository" },
        });

        var result = PlanValidator.Validate(plan);

        Assert.Equal(2, result.Errors.Count(e => e.Contains("must appear EARLIER")));
        // The package itself is fine otherwise: no unrelated structural errors are invented.
        Assert.DoesNotContain(result.Errors, e => e.Contains("'WP-003': dependency 'WP-004' does not exist"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("'WP-003': dependency 'WP-005' does not exist"));
    }

    [Fact]
    public void The_ordering_rule_coexists_with_cycle_and_layer_checks()
    {
        var plan = new Plan
        {
            ProjectName = "Mixed",
            WorkPackages =
            {
                new WorkPackage
                {
                    Id = "WP-001", Layer = "DOMAIN", Title = "t", Goal = "g",
                    Dependencies = { "WP-002" }, Directives = { "d" },
                },
                new WorkPackage { Id = "WP-002", Layer = "APP", Title = "t", Goal = "g", Directives = { "d" } },
            },
        };
        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("must appear EARLIER"));
        Assert.Contains(result.Errors, e => e.Contains("lower layer must never depend on a higher layer"));
    }

    // ---- collection bounds -------------------------------------------------------------------

    [Fact]
    public void Excessive_work_package_counts_are_rejected()
    {
        var plan = new Plan { ProjectName = "Flooded" };
        for (var i = 0; i <= PlanValidator.MaxWorkPackages; i++)
            plan.WorkPackages.Add(new WorkPackage
            {
                Id = $"WP-{i:D3}",
                Layer = "INFRA",
                Title = "t",
                Goal = "g",
                Directives = { "d" },
            });

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains($"plan exceeds {PlanValidator.MaxWorkPackages} work packages"));
    }

    [Fact]
    public void Excessive_dependency_counts_are_rejected()
    {
        var plan = new Plan { ProjectName = "Wide" };
        for (var i = 0; i < 70; i++)
        {
            var id = $"WP-{(i + 10):D3}";
            plan.WorkPackages.Add(new WorkPackage
            {
                Id = id,
                Layer = "INFRA",
                Title = "t",
                Goal = "g",
                Directives = { "d" },
            });
        }
        plan.WorkPackages.Add(new WorkPackage
        {
            Id = "WP-009",
            Layer = "INFRA",
            Title = "t",
            Goal = "g",
            Directives = { "d" },
            Dependencies = plan.WorkPackages.Take(65).Select(w => w.Id).ToList(),
        });

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e =>
            e.Contains($"more than {PlanValidator.MaxDependenciesPerPackage} dependencies"));
    }

    [Fact]
    public void Null_status_is_a_controlled_error()
    {
        var plan = ValidPlan();
        plan.WorkPackages[0].Status = null!;

        var result = PlanValidator.Validate(plan);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unknown or non-canonical status"));
    }

    // ---- a valid plan round trips through the strict store --------------------------------------

    [Fact]
    public void A_valid_plan_round_trips_through_the_strict_store()
    {
        var plan = ValidPlan();
        plan.ArchitectureMap = new ArchitectureMap { BoundedContexts = { "Core" } };
        _store.Save(plan);

        var loaded = _store.Load();

        Assert.Equal("Demo", loaded.ProjectName);
        Assert.Equal(2, loaded.WorkPackages.Count);
        Assert.Equal("Core", loaded.ArchitectureMap!.BoundedContexts.Single());
    }
}
