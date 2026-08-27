using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Validation;

namespace Tenninety.Tests;

public static class TestPlans
{
    public static WorkPackage Wp(string id, string layer = "DOMAIN", params string[] deps) => new()
    {
        Id = id,
        Layer = layer,
        Title = $"{id} title",
        Goal = $"{id} goal",
        Dependencies = deps.ToList(),
        Directives = new List<string> { "do the thing" },
        AcceptanceCriteria = new List<string> { "thing is done" },
        Status = TenNinety.WpStatus.Pending,
    };

    public static Plan Simple() => new()
    {
        ProjectName = "Demo",
        GlobalContext = new GlobalContext { TechStack = ".NET 10" },
        WorkPackages =
        {
            Wp("WP-001", "INFRA"),
            Wp("WP-002", "DOMAIN", "WP-001"),
            Wp("WP-003", "DATA", "WP-002"),
        },
    };
}

public class PlanValidatorTests
{
    [Fact]
    public void Valid_plan_passes_with_no_errors()
    {
        var result = PlanValidator.Validate(TestPlans.Simple());
        Assert.True(result.IsValid, string.Join(";", result.Errors));
    }

    [Fact]
    public void Cycle_is_rejected_strict_dag_rule()
    {
        var plan = new Plan
        {
            ProjectName = "Cyclic",
            WorkPackages =
            {
                TestPlans.Wp("WP-001", deps: "WP-002"),
                TestPlans.Wp("WP-002", deps: "WP-001"),
            },
        };
        var result = PlanValidator.Validate(plan);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("cycle"));
    }

    [Fact]
    public void Missing_dependency_is_an_error()
    {
        var plan = TestPlans.Simple();
        plan.WorkPackages[1].Dependencies.Add("WP-999");
        var result = PlanValidator.Validate(plan);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("WP-999"));
    }

    [Fact]
    public void Duplicate_ids_are_rejected()
    {
        var plan = new Plan
        {
            ProjectName = "Dup",
            WorkPackages = { TestPlans.Wp("WP-001"), TestPlans.Wp("WP-001") },
        };
        Assert.False(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Wrong_schema_version_is_rejected()
    {
        var plan = TestPlans.Simple();
        plan.SchemaVersion = "9.9";
        Assert.False(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Empty_directives_violate_atomic_decomposition()
    {
        var plan = TestPlans.Simple();
        plan.WorkPackages[0].Directives.Clear();
        Assert.False(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Conflict_wps_may_have_no_directives_but_are_flagged()
    {
        // Blueprint v3.2 Enterprise: contradictory spec rules ⇒ no directives, human resolves later.
        var plan = TestPlans.Simple();
        var wp = plan.WorkPackages[1];
        wp.Directives.Clear();
        wp.Notes = "CONFLICT: spec requires both 'no auth' and 'users own tasks'. Human must resolve.";

        var result = PlanValidator.Validate(plan);
        Assert.True(result.IsValid, string.Join(";", result.Errors));
        Assert.Contains(result.Warnings, w => w.Contains("WP-002") && w.Contains("CONFLICT"));
    }

    [Fact]
    public void Ambiguous_wps_are_surfaced_as_warnings()
    {
        var plan = TestPlans.Simple();
        plan.WorkPackages[0].Notes = "AMBIGUOUS: DB engine unspecified; assumed PostgreSQL per industry default.";

        var result = PlanValidator.Validate(plan);
        Assert.True(result.IsValid);
        Assert.Contains(result.Warnings, w => w.Contains("WP-001") && w.Contains("AMBIGUOUS"));
    }

    [Theory]
    [InlineData("We are UNAMBIGUOUS about this")]
    [InlineData("Avoid CONFLICTING guidance")]
    [InlineData("the conflict was resolved offline")]   // lowercase prose never triggers
    public void Marker_lookalikes_in_prose_do_not_trigger_the_protocol(string notes)
    {
        var plan = TestPlans.Simple();
        plan.WorkPackages[0].Directives.Clear(); // would only be legal with a real CONFLICT marker
        plan.WorkPackages[0].Notes = notes;
        Assert.False(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Lower_layer_dependending_on_higher_layer_is_a_hard_error()
    {
        // Blueprint v3.2 Enterprise rule 4: lower layers must never depend on higher ones.
        var plan = new Plan
        {
            ProjectName = "Inverted",
            WorkPackages =
            {
                TestPlans.Wp("WP-001", "DOMAIN", deps: "WP-002"),
                TestPlans.Wp("WP-002", "APP"),
            },
        };
        var result = PlanValidator.Validate(plan);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("lower layer must never depend on a higher layer"));
    }

    [Fact]
    public void Higher_layer_dependending_on_lower_layer_is_fine()
    {
        var plan = new Plan
        {
            ProjectName = "Ordered",
            WorkPackages =
            {
                TestPlans.Wp("WP-001", "INFRA"),
                TestPlans.Wp("WP-002", "TEST-E2E", deps: "WP-001"),
            },
        };
        Assert.True(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Ui_service_is_presentation_layer_and_may_depend_on_api()
    {
        var plan = new Plan
        {
            ProjectName = "UI",
            WorkPackages =
            {
                TestPlans.Wp("WP-001", "API"),
                TestPlans.Wp("WP-002", "UI-SERVICE", "WP-001"),
            },
        };

        Assert.True(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Domain_cannot_depend_on_ui_infrastructure()
    {
        var plan = new Plan
        {
            ProjectName = "Inverted UI",
            WorkPackages =
            {
                TestPlans.Wp("WP-001", "UI-INFRA"),
                TestPlans.Wp("WP-002", "DOMAIN", "WP-001"),
            },
        };

        Assert.False(PlanValidator.Validate(plan).IsValid);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("BOGUS")]
    [InlineData("")]
    public void Noncanonical_statuses_are_rejected(string status)
    {
        var plan = TestPlans.Simple();
        plan.WorkPackages[0].Status = status;

        Assert.False(PlanValidator.Validate(plan).IsValid);
    }

    [Fact]
    public void Topological_order_respects_dependencies_and_natural_ids()
    {
        var plan = new Plan
        {
            ProjectName = "Order",
            WorkPackages =
            {
                TestPlans.Wp("WP-010", deps: "WP-002"),
                TestPlans.Wp("WP-002"),
            },
        };
        var order = PlanValidator.TopologicalOrder(plan);
        Assert.NotNull(order);
        Assert.True(order!.IndexOf("WP-002") < order.IndexOf("WP-010"));
    }
}
