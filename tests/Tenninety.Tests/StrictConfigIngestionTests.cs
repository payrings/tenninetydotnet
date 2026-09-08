using Tenninety.Core.Models;
using Tenninety.Core.Stores;

namespace Tenninety.Tests;

/// <summary>
/// Strict bounded configuration ingestion: unknown members (typos such as "provider_mod"),
/// duplicate fields at every depth, explicit nulls, excessive size/depth and out-of-range
/// numbers must fail with a clear error naming the owning field — never by silently
/// selecting a default. Omitted fields legitimately keep their defaults, and a valid config
/// round trips unchanged.
/// </summary>
public sealed class StrictConfigIngestionTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ConfigStore _store;

    public StrictConfigIngestionTests() => _store = new ConfigStore(_tmp.Path("config.json"));

    public void Dispose() => _tmp.Dispose();

    private void Write(string json) => File.WriteAllText(_tmp.Path("config.json"), json);

    private InvalidOperationException LoadError()
    {
        var ex = Assert.Throws<InvalidOperationException>(_store.Load);
        return ex;
    }

    // ---- unknown members ------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "provider_mod": "aider" }""")]
    [InlineData("""{ "sandbox": { "mode": "docker", "mod": "x" } }""")]
    [InlineData("""{ "sandbox": { "roles": { "coder": { "imag": "x" } } } }""")]
    [InlineData("""{ "aider": { "model": "", "extra_arg": "" } }""")]
    public void Unknown_members_are_rejected_with_the_owning_context(string json)
    {
        Write(json);
        var ex = LoadError();
        Assert.Contains("config.json", ex.Message);
        Assert.Contains("Unknown", ex.Message);
    }

    [Fact]
    public void A_provider_mod_typo_can_never_select_a_provider_mode()
    {
        Write("""{ "provider_mod": "aider" }""");
        var ex = LoadError();
        Assert.Contains("provider_mod", ex.Message);
        // The error names the offending member, not a silent default.
        Assert.DoesNotContain("unknown provider_mode", ex.Message);
    }

    // ---- duplicate fields -----------------------------------------------------------------

    [Fact]
    public void Duplicate_field_at_the_top_level_is_rejected()
    {
        Write("""{ "provider_mode": "mock", "provider_mode": "aider" }""");
        var ex = LoadError();
        Assert.Contains("duplicate field 'provider_mode'", ex.Message);
    }

    [Fact]
    public void Duplicate_field_in_a_nested_object_is_rejected()
    {
        Write("""
            {
              "local_models": { "coder": "a", "coder": "b" }
            }
            """);
        var ex = LoadError();
        Assert.Contains("duplicate field 'coder'", ex.Message);
        Assert.Contains("local_models", ex.Message);
    }

    [Fact]
    public void Duplicate_field_deep_inside_the_sandbox_contract_is_rejected()
    {
        Write("""
            {
              "sandbox": {
                "roles": {
                  "tester": {
                    "restore": { "enabled": false, "enabled": true }
                  }
                }
              }
            }
            """);
        var ex = LoadError();
        Assert.Contains("duplicate field 'enabled'", ex.Message);
        Assert.Contains("sandbox.roles.tester.restore", ex.Message);
    }

    // ---- path tracker: errors in SIBLING properties after nested values ---------------------

    [Fact]
    public void Duplicate_sibling_after_a_nested_object_is_reported_at_the_parent_path()
    {
        // Regression: the path tracker once leaked every nested-value owner into the path,
        // so an error on the sibling 'a' would have been misattributed to 'a.inner'.
        Write("""
            {
              "local_models": { "coder": "a" },
              "local_models": { "reviewer": "b" }
            }
            """);
        var ex = LoadError();
        Assert.Contains("duplicate field 'local_models'", ex.Message);
        // The owning path is the ROOT object, not the nested one.
        Assert.Contains("at '<root>'", ex.Message);
    }

    [Fact]
    public void Duplicate_sibling_after_a_nested_array_is_reported_at_the_parent_path()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""
            { "allow": ["x", "y"], "allow": 3 }
            """);
        var ex = Assert.Throws<InvalidOperationException>(
            () => StrictJsonIngestion.EnsureStrictShape(bytes, 1024, "shape-test"));
        Assert.Contains("duplicate field 'allow'", ex.Message);
        Assert.Contains("at '<root>'", ex.Message);
    }

    [Fact]
    public void Sibling_error_after_a_deeply_nested_value_names_the_correct_owning_path()
    {
        // The overlong property name appears on the sibling of a deeply nested object value;
        // the reported path must name the object that OWNS the sibling, not the nested one.
        var longName = new string('n', StrictJsonIngestion.MaxPropertyNameChars + 1);
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            $$"""{ "sandbox": { "mode": "docker" }, "{{longName}}": 1 }""");
        var ex = Assert.Throws<InvalidOperationException>(
            () => StrictJsonIngestion.EnsureStrictShape(bytes, 4096, "shape-test"));
        Assert.Contains("property name longer than", ex.Message);
        Assert.Contains("at '<root>'", ex.Message);
    }

    [Fact]
    public void Deep_duplicate_after_a_nested_object_is_still_found()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("""
            {
              "outer": { "inner": { "deep": 1 } },
              "outer": { "inner": { "deep": 2 } }
            }
            """);
        var ex = Assert.Throws<InvalidOperationException>(
            () => StrictJsonIngestion.EnsureStrictShape(bytes, 4096, "shape-test"));
        Assert.Contains("duplicate field 'outer'", ex.Message);
        Assert.Contains("at '<root>'", ex.Message);
    }

    // ---- explicit nulls --------------------------------------------------------------------

    [Theory]
    [InlineData("""{ "provider_mode": null }""", "provider_mode")]
    [InlineData("""{ "build_command": null }""", "build_command")]
    [InlineData("""{ "test_command": null }""", "test_command")]
    [InlineData("""{ "llama_swap_coder_endpoint": null }""", "llama_swap_coder_endpoint")]
    [InlineData("""{ "aider": { "model": null } }""", "aider.model")]
    public void Explicit_null_strings_are_rejected_naming_the_field(string json, string field)
    {
        Write(json);
        var ex = LoadError();
        Assert.Contains($"'{field}'", ex.Message);
        Assert.Contains("null", ex.Message);
    }

    [Fact]
    public void Explicit_null_nested_object_is_rejected()
    {
        Write("""{ "local_models": null }""");
        var ex = LoadError();
        Assert.Contains("null", ex.Message);
    }

    [Fact]
    public void Explicit_null_sandbox_section_is_rejected_never_defaulted()
    {
        Write("""{ "sandbox": null }""");
        var ex = LoadError();
        Assert.Contains("null sandbox", ex.Message);
    }

    [Fact]
    public void Explicit_null_collection_is_rejected()
    {
        Write("""{ "sandbox": { "promotion": { "allow_sensitive_paths": null } } }""");
        var ex = LoadError();
        Assert.Contains("allow_sensitive_paths", ex.Message);
        Assert.Contains("null", ex.Message);
    }

    // ---- size and depth bounds --------------------------------------------------------------

    [Fact]
    public void An_oversized_config_fails_the_size_bound_before_parsing()
    {
        // 1 MiB + padding of a harmless (but rejected) duplicate-free document.
        var padding = new string('x', checked((int)StrictJsonIngestion.MaxConfigBytes + 1));
        Write($$"""{ "project_note": "{{padding}}" }""");
        var ex = LoadError();
        Assert.Contains("size bound", ex.Message);
    }

    [Fact]
    public void Bounded_read_requests_and_consumes_at_most_the_limit_plus_one_byte()
    {
        using var stream = new CountingReadStream(new byte[100]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => StrictJsonIngestion.ReadBounded(stream, 10, "counting contract"));

        Assert.Contains("size bound", ex.Message);
        Assert.Equal(11, stream.BytesRead);
        Assert.Equal([11], stream.RequestedCounts);
    }

    [Fact]
    public void Excessive_nesting_depth_is_rejected()
    {
        var depth = StrictJsonIngestion.MaxDepth + 4;
        var json = new string('[', depth) + new string(']', depth);
        Write(json);
        var ex = LoadError();
        Assert.Contains("depth", ex.Message);
    }

    // ---- numeric bounds ----------------------------------------------------------------------

    [Fact]
    public void Out_of_range_numbers_are_rejected_naming_the_field()
    {
        Write("""{ "max_total_attempts": 999999 }""");
        var ex = LoadError();
        Assert.Contains("max_total_attempts", ex.Message);

        Write("""{ "attempt_timeout_minutes": 999999 }""");
        var ex2 = LoadError();
        Assert.Contains("attempt_timeout_minutes", ex2.Message);
    }

    [Theory]
    [InlineData("""{ "max_total_attempts": 0 }""", "max_total_attempts")]
    [InlineData("""{ "max_total_attempts": -20 }""", "max_total_attempts")]
    [InlineData("""{ "max_attempts_before_escalation": 0 }""", "max_attempts_before_escalation")]
    [InlineData("""{ "max_attempts_before_escalation": -10 }""", "max_attempts_before_escalation")]
    [InlineData("""{ "attempt_timeout_minutes": 0 }""", "attempt_timeout_minutes")]
    [InlineData("""{ "attempt_timeout_minutes": -10 }""", "attempt_timeout_minutes")]
    [InlineData("""{ "max_concurrent_workers": 0 }""", "max_concurrent_workers")]
    public void Explicit_zero_or_negative_values_are_rejected_never_clamped(string json, string field)
    {
        Write(json);
        var ex = LoadError();
        Assert.Contains(field, ex.Message);
        Assert.Contains("but is", ex.Message);
    }

    [Fact]
    public void Omitted_numeric_values_keep_their_documented_defaults()
    {
        Write("""{ "provider_mode": "mock" }""");
        var config = _store.Load();
        Assert.Equal(10, config.MaxAttemptsBeforeEscalation);
        Assert.Equal(20, config.MaxTotalAttempts);
        Assert.Equal(10, config.AttemptTimeoutMinutes);
        Assert.Equal(1, config.MaxConcurrentWorkers);
    }

    // ---- retry-threshold relationships (issue 8) ----------------------------------------------

    [Fact]
    public void Equal_retry_thresholds_are_rejected_because_escalation_could_never_fire()
    {
        var config = new TenNinetyConfig
        {
            MaxAttemptsBeforeEscalation = 10,
            MaxTotalAttempts = 10,
        };
        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("max_attempts_before_escalation", ex.Message);
        Assert.Contains("max_total_attempts", ex.Message);
    }

    [Fact]
    public void Reversed_retry_thresholds_are_rejected()
    {
        var config = new TenNinetyConfig
        {
            MaxAttemptsBeforeEscalation = 30,
            MaxTotalAttempts = 20,
        };
        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("strictly less", ex.Message);
    }

    [Fact]
    public void Adjacent_valid_thresholds_are_accepted()
    {
        var config = new TenNinetyConfig
        {
            MaxAttemptsBeforeEscalation = 1,
            MaxTotalAttempts = 2,
        };
        config.Validate(); // boundary: exactly one escalation is possible

        Assert.True(new TenNinetyConfig { MaxAttemptsBeforeEscalation = 9, MaxTotalAttempts = 10 } is var ok);
        ok.Validate();
    }

    [Fact]
    public void Threshold_relationships_are_enforced_on_load_from_disk()
    {
        Write("""{ "max_attempts_before_escalation": 5, "max_total_attempts": 5 }""");
        var ex = LoadError();
        Assert.Contains("strictly less", ex.Message);
    }

    // ---- compatibility: omitted fields keep their defaults -------------------------------------

    [Fact]
    public void A_legacy_config_with_omitted_fields_loads_with_defaults()
    {
        // The oldest valid configs only carried a handful of fields; everything else defaults
        // (provider mock, docker sandbox, llama-swap disabled, thresholds 10/20).
        Write("""
            {
              "provider_mode": "mock",
              "local_models": { "coder": "c", "reviewer": "r" }
            }
            """);
        var config = _store.Load();

        Assert.Equal("mock", config.ProviderMode);
        Assert.Equal("docker", config.Sandbox.Mode);
        Assert.False(config.UseLlamaSwap);
        Assert.Equal(10, config.MaxAttemptsBeforeEscalation);
        Assert.Equal(20, config.MaxTotalAttempts);
        Assert.Equal("aider", config.CoderAgent);
        Assert.Equal("dotnet build", config.BuildCommand);
    }

    [Fact]
    public void A_valid_full_config_round_trips_through_the_store()
    {
        var config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            UseLlamaSwap = true,
            Sandbox = LiveSandbox(),
        };
        _store.Save(config);
        var loaded = _store.Load();

        Assert.Equal("aider", loaded.ProviderMode);
        Assert.True(loaded.UseLlamaSwap);
        Assert.Equal(config.Sandbox.Roles.Coder.Image, loaded.Sandbox.Roles.Coder.Image);
        Assert.Equal(
            config.Sandbox.Roles.Tester.Restore.ApprovedFeeds,
            loaded.Sandbox.Roles.Tester.Restore.ApprovedFeeds);
        Assert.Equal(10, loaded.MaxAttemptsBeforeEscalation);
    }

    [Fact]
    public void An_empty_file_is_rejected_and_a_missing_file_defaults()
    {
        File.WriteAllText(_tmp.Path("config.json"), "");
        Assert.Throws<InvalidOperationException>(_store.Load);

        File.Delete(_tmp.Path("config.json"));
        var config = _store.Load();
        Assert.Equal("mock", config.NormalizedProviderMode);
    }

    private static SandboxConfig LiveSandbox() => new()
    {
        Roles = new SandboxRolesConfig
        {
            Coder = new CoderSandboxRoleConfig
            {
                Image = "sha256:" + new string('a', 64),
            },
            Reviewer = new ReviewerSandboxRoleConfig
            {
                Image = "sha256:" + new string('b', 64),
            },
            Tester = new TesterSandboxRoleConfig
            {
                Image = "sha256:" + new string('c', 64),
                Restore = new SandboxRestoreConfig
                {
                    Enabled = false,
                    ApprovedFeeds = ["https://api.nuget.org/v3/index.json"],
                },
            },
        },
    };

    private sealed class CountingReadStream(byte[] content) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }
        public List<int> RequestedCounts { get; } = [];
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            RequestedCounts.Add(count);
            var read = Math.Min(count, content.Length - _position);
            content.AsSpan(_position, read).CopyTo(buffer.AsSpan(offset, read));
            _position += read;
            BytesRead += read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
