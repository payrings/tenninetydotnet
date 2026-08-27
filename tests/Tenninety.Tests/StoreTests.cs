using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Stores;

namespace Tenninety.Tests;

public class StoreRoundTripTests
{
    [Fact]
    public void Plan_serializes_with_spec_compliant_shape()
    {
        var plan = TestPlans.Simple();
        var json = Json.Serialize(plan);

        Assert.Contains("\"schema_version\": \"3.2\"", json);
        Assert.Contains("\"project_name\": \"Demo\"", json);
        Assert.Contains("\"global_context\"", json);
        Assert.Contains("\"work_packages\"", json);
        Assert.Contains("\"acceptance_criteria\"", json);
    }

    [Fact]
    public void Plan_round_trips_enterprise_blueprint_fields()
    {
        // Blueprint v3.2 Enterprise: architecture_map, directory_structure, module, notes.
        var plan = TestPlans.Simple();
        plan.ArchitectureMap = new ArchitectureMap
        {
            BoundedContexts = { "Identity", "Tasks" },
            CoreEntities = { "User", "Task" },
            KeyDependencies = { "Tasks depend on Identity for ownership" },
        };
        plan.GlobalContext.DirectoryStructure = new Dictionary<string, List<string>>
        {
            ["src"] = new() { "Demo.Core", "Demo.Api" },
            ["tests"] = new() { "Demo.UnitTests" },
        };
        plan.WorkPackages[0].Module = "Identity";
        plan.WorkPackages[0].Notes = "";

        using var tmp = new TempDir();
        var store = new PlanStore(tmp.Path("plan.json"));
        store.Save(plan);
        var loaded = store.Load();

        Assert.Equal(["Identity", "Tasks"], loaded.ArchitectureMap!.BoundedContexts);
        Assert.Equal("Demo.Core", loaded.GlobalContext.DirectoryStructure!["src"][0]);
        Assert.Equal("Identity", loaded.WorkPackages[0].Module);
    }

    [Fact]
    public void Plan_json_uses_blueprint_field_names()
    {
        var plan = TestPlans.Simple();
        plan.ArchitectureMap = new ArchitectureMap { BoundedContexts = { "Core" } };
        plan.GlobalContext.DirectoryStructure = new Dictionary<string, List<string>> { ["src"] = new() { "X" } };
        plan.WorkPackages[0].Module = "Core";
        plan.WorkPackages[0].Notes = "note";
        var json = Json.Serialize(plan);

        Assert.Contains("\"architecture_map\"", json);
        Assert.Contains("\"bounded_contexts\"", json);
        Assert.Contains("\"directory_structure\"", json);
        Assert.Contains("\"module\": \"Core\"", json);
        Assert.Contains("\"notes\": \"note\"", json);
    }

    [Fact]
    public void State_round_trips_attempts_and_queue()
    {
        var state = new RuntimeState
        {
            CurrentWp = "WP-101",
            ExecutionMode = "serial",
            QueueStatus = new Dictionary<string, string> { ["WP-001"] = "DONE", ["WP-101"] = "ACTIVE" },
            Attempts =
            {
                ["WP-101"] = new AttemptInfo
                {
                    Count = 4,
                    Max = 10,
                    Total = 4,
                    LastFailureType = TenNinety.FailureTypes.Reviewer,
                    LastFailureReasons = new List<string> { "Missing null check" },
                },
            },
        };

        using var tmp = new TempDir();
        var store = new StateStore(tmp.Path("state.json"));
        store.Save(state);
        var loaded = store.Load();

        Assert.Equal("WP-101", loaded.CurrentWp);
        Assert.Equal("DONE", loaded.QueueStatus["WP-001"]);
        Assert.Equal(4, loaded.Attempts["WP-101"].Count);
        Assert.Equal(TenNinety.FailureTypes.Reviewer, loaded.Attempts["WP-101"].LastFailureType);
    }

    [Fact]
    public void Config_defaults_match_spec_examples()
    {
        var json = Json.Serialize(new TenNinetyConfig());
        Assert.Contains("\"execution_mode\": \"serial\"", json);
        Assert.Contains("\"max_concurrent_workers\": 1", json);
        Assert.Contains("\"coder\"", json);
        Assert.Contains("\"frontier_endpoint\"", json);
    }

    [Fact]
    public void Config_load_rejects_unknown_provider_modes()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("config.json"), "{\"provider_mode\":\"mokc\"}");

        var ex = Assert.Throws<InvalidOperationException>(
            () => new ConfigStore(tmp.Path("config.json")).Load());

        Assert.Contains("unknown provider_mode", ex.Message);
    }

    [Fact]
    public void Atomic_state_update_preserves_existing_progress()
    {
        using var tmp = new TempDir();
        var store = new StateStore(tmp.Path("state.json"));
        store.Save(new RuntimeState
        {
            CurrentWp = "WP-001",
            QueueStatus = { ["WP-001"] = TenNinety.WpStatus.Active },
            Attempts = { ["WP-001"] = new AttemptInfo { Total = 3 } },
        });

        store.Update(state => state.Paused = true);

        var updated = store.Load();
        Assert.True(updated.Paused);
        Assert.Equal("WP-001", updated.CurrentWp);
        Assert.Equal(3, updated.Attempts["WP-001"].Total);
    }

    [Theory]
    [InlineData("{\"attempts\":null,\"queue_status\":{}}")]
    [InlineData("{\"attempts\":{\"WP-001\":{\"count\":0,\"max\":10,\"total\":2147483647}},\"queue_status\":{}}")]
    public void State_load_rejects_malformed_runtime_data(string json)
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("state.json"), json);

        Assert.Throws<InvalidOperationException>(() => new StateStore(tmp.Path("state.json")).Load());
    }

    [Fact]
    public void Audit_log_is_jsonl_one_compact_line_per_event()
    {
        using var tmp = new TempDir();
        var audit = new AuditLog(tmp.Path("audit-log.jsonl"));
        audit.Append("WP_STARTED", "WP-001", "branch=work/WP-001");
        audit.Append("WP_PROMOTED", "WP-001", "merge=abc");

        var lines = File.ReadAllLines(audit.Path, System.Text.Encoding.UTF8);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain('\n', lines[0]);
        foreach (var line in lines)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.True(doc.RootElement.TryGetProperty("event", out _));
        }
    }
}

public sealed class TempDir : IDisposable
{
    public string Root { get; } =
        Directory.CreateTempSubdirectory("tenninety-tests").FullName;

    public string Path(string name) => System.IO.Path.Combine(Root, name);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
