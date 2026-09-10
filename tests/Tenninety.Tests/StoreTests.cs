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

        Assert.Contains("\"schema_version\": \"1\"", json);
        Assert.Contains("\"project_name\": \"Demo\"", json);
        Assert.Contains("\"global_context\"", json);
        Assert.Contains("\"work_packages\"", json);
        Assert.Contains("\"acceptance_criteria\"", json);
    }

    [Fact]
    public void Plan_round_trips_blueprint_fields()
    {
        // Blueprint: architecture_map, directory_structure, module, notes.
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
    public void Config_defaults_carry_the_llama_swap_endpoints_and_model_identifiers()
    {
        var json = Json.Serialize(new TenNinetyConfig());
        Assert.Contains("\"llama_swap_endpoint\": \"http://localhost:8080/v1\"", json);
        Assert.Contains("\"llama_swap_coder_endpoint\": \"http://llama-swap:8080/v1\"", json);
        Assert.Contains("\"use_llama_swap\": false", json);
        Assert.Contains("\"coder\": \"coder\"", json);
        Assert.Contains("\"reviewer\": \"reviewer\"", json);
    }

    [Fact]
    public void Legacy_config_without_llama_swap_coder_endpoint_loads_with_the_default()
    {
        // Backward compatibility: configs written before llama_swap_coder_endpoint existed
        // must deserialize to the Docker Compose default instead of an empty endpoint.
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Path("config.json"),
            """{"provider_mode":"aider","use_llama_swap":true}""");

        var config = new ConfigStore(tmp.Path("config.json")).Load();

        Assert.True(config.UseLlamaSwap);
        Assert.Equal("http://llama-swap:8080/v1", config.LlamaSwapCoderEndpoint);
    }

    [Fact]
    public void Llama_swap_endpoints_round_trip()
    {
        using var tmp = new TempDir();
        var path = tmp.Path("config.json");
        var store = new ConfigStore(path);
        var config = store.Exists() ? store.Load() : new TenNinetyConfig();
        config.LlamaSwapEndpoint = "http://127.0.0.1:18080/v1";
        config.LlamaSwapCoderEndpoint = "http://model-proxy.internal:8080/v1";
        store.Save(config);

        var loaded = new ConfigStore(path).Load();

        Assert.Equal("http://127.0.0.1:18080/v1", loaded.LlamaSwapEndpoint);
        Assert.Equal("http://model-proxy.internal:8080/v1", loaded.LlamaSwapCoderEndpoint);
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

    [Fact]
    public void Audit_log_sanitizes_and_bounds_every_public_text_field()
    {
        using var tmp = new TempDir();
        const string secret = "supersecretvalue123";
        var hostile = "safe\u001b[31m\r\0\u0085 apiKey=" + secret + " " +
                      new string('x', AuditLog.MaxDetailChars + 1000);
        var audit = new AuditLog(tmp.Path("audit-log.jsonl"));

        audit.Append("EVENT\u001b", "WP\r001", hostile);

        var entry = Assert.Single(audit.ReadTail());
        Assert.Equal("EVENT", entry.Event);
        Assert.Equal("WP001", entry.WorkPackageId);
        Assert.True(entry.Detail.Length <= AuditLog.MaxDetailChars);
        Assert.DoesNotContain(secret, entry.Detail, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', entry.Detail);
        Assert.DoesNotContain('\r', entry.Detail);
        Assert.DoesNotContain('\0', entry.Detail);
        Assert.DoesNotContain('\u0085', entry.Detail);
    }

    [Fact]
    public void Audit_tail_returns_exact_requested_suffix_in_chronological_order()
    {
        using var tmp = new TempDir();
        var audit = new AuditLog(tmp.Path("audit-log.jsonl"));
        for (var i = 0; i < 8; i++) audit.Append("EVENT-" + i);

        var entries = audit.ReadTail(3);

        Assert.Equal(["EVENT-5", "EVENT-6", "EVENT-7"],
            entries.Select(entry => entry.Event));
    }

    [Fact]
    public void Audit_tail_reads_a_small_suffix_of_a_large_file()
    {
        using var tmp = new TempDir();
        var path = tmp.Path("audit-log.jsonl");
        var lines = Enumerable.Range(0, 20_000)
            .Select(index => AuditJson("EVENT-" + index, new string('x', 80)));
        File.WriteAllText(path, string.Join('\n', lines) + "\n",
            new System.Text.UTF8Encoding(false));

        var entries = new AuditLog(path).ReadTail(2);

        Assert.Equal(["EVENT-19998", "EVENT-19999"],
            entries.Select(entry => entry.Event));
    }

    [Fact]
    public void Audit_tail_supports_crlf_and_an_unterminated_final_record()
    {
        using var tmp = new TempDir();
        var path = tmp.Path("audit-log.jsonl");
        File.WriteAllText(path,
            AuditJson("FIRST") + "\r\n" + AuditJson("SECOND") + "\r\n" +
            AuditJson("THIRD"),
            new System.Text.UTF8Encoding(false));

        var entries = new AuditLog(path).ReadTail(3);

        Assert.Equal(["FIRST", "SECOND", "THIRD"],
            entries.Select(entry => entry.Event));
    }

    [Fact]
    public void Malformed_unterminated_tail_record_keeps_the_sanitized_unparseable_shape()
    {
        using var tmp = new TempDir();
        const string secret = "supersecretvalue123";
        var path = tmp.Path("audit-log.jsonl");
        File.WriteAllText(path,
            AuditJson("FIRST") + "\napiKey=" + secret + "\u001b[31m",
            new System.Text.UTF8Encoding(false));

        var entries = new AuditLog(path).ReadTail(2);

        Assert.Equal("FIRST", entries[0].Event);
        Assert.Equal("UNPARSEABLE", entries[1].Event);
        Assert.DoesNotContain(secret, entries[1].Detail);
        Assert.Contains("[REDACTED]", entries[1].Detail);
        Assert.DoesNotContain('\u001b', entries[1].Detail);
    }

    [Fact]
    public void Giant_corrupt_record_is_withheld_and_stops_before_unbounded_scanning()
    {
        using var tmp = new TempDir();
        var path = tmp.Path("audit-log.jsonl");
        File.WriteAllText(path,
            new string('x', AuditLog.MaxRecordBytes * 32) + "\n" + AuditJson("LATEST"),
            new System.Text.UTF8Encoding(false));

        var entries = new AuditLog(path).ReadTail(2);

        Assert.Equal(2, entries.Count);
        Assert.Equal("UNPARSEABLE", entries[0].Event);
        Assert.Contains($"{AuditLog.MaxRecordBytes}-byte limit", entries[0].Detail);
        Assert.Contains("content withheld", entries[0].Detail);
        Assert.Equal("LATEST", entries[1].Event);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Nonpositive_audit_tail_count_is_deterministically_empty(int count)
    {
        using var tmp = new TempDir();
        var path = tmp.Path("audit-log.jsonl");
        File.WriteAllText(path, new string('x', AuditLog.MaxRecordBytes * 2));

        Assert.Empty(new AuditLog(path).ReadTail(count));
    }

    private static string AuditJson(string eventName, string detail = "") =>
        Json.SerializeCompact(new AuditEvent
        {
            Timestamp = "2026-01-01T00:00:00.0000000+00:00",
            Event = eventName,
            Detail = detail,
        });
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
