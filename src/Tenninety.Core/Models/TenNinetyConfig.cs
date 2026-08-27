using System.Text.Json.Serialization;

namespace Tenninety.Core.Models;

/// <summary>Configuration persisted to .tenninety/config.json (Part III.4). Secrets are read from env, never stored here.</summary>
public sealed class TenNinetyConfig
{
    [JsonPropertyName("execution_mode")]
    public string ExecutionMode { get; set; } = "serial";

    [JsonPropertyName("max_concurrent_workers")]
    public int MaxConcurrentWorkers { get; set; } = 1;

    [JsonPropertyName("local_models")]
    public LocalModelsConfig LocalModels { get; set; } = new();

    [JsonPropertyName("frontier_endpoint")]
    public string FrontierEndpoint { get; set; } = "https://api.frontier.ai/v1";

    /// <summary>"mock" (offline simulation) or "openai-compatible" (local model servers).</summary>
    [JsonPropertyName("provider_mode")]
    public string ProviderMode { get; set; } = "mock";

    [JsonPropertyName("frontier_model")]
    public string FrontierModel { get; set; } = "frontier-architect";

    /// <summary>Name of the env var holding the frontier API key. The key itself is never persisted.</summary>
    [JsonPropertyName("frontier_api_key_env")]
    public string FrontierApiKeyEnv { get; set; } = "TENNINETY_FRONTIER_API_KEY";

    /// <summary>Base URL of the OpenAI-compatible endpoint serving the local coder/reviewer models.</summary>
    [JsonPropertyName("local_models_endpoint")]
    public string LocalModelsEndpoint { get; set; } = "http://localhost:8000/v1";

    /// <summary>
    /// Human-settable switch: route both local models through a llama-swap proxy so the coder
    /// and reviewer can share one GPU card (models are swapped on demand by name).
    /// When true, <see cref="LlamaSwapEndpoint"/> is used instead of <see cref="LocalModelsEndpoint"/>.
    /// </summary>
    [JsonPropertyName("attempt_timeout_minutes")]
    public int AttemptTimeoutMinutes
    {
        get => field;
        set => field = Math.Max(1, value);
    } = 10;

    [JsonPropertyName("use_llama_swap")]
    public bool UseLlamaSwap { get; set; }

    [JsonPropertyName("llama_swap_endpoint")]
    public string LlamaSwapEndpoint { get; set; } = "http://localhost:8080/v1";

    /// <summary>
    /// Which terminal coding agent plays the Coder role in live mode:
    /// "aider" (default), "opencode" or "pi". All three edit the working tree and
    /// never commit – the engine owns every commit.
    /// </summary>
    [JsonPropertyName("coder_agent")]
    public string CoderAgent { get; set; } = "aider";

    /// <summary>Settings for the OpenCode CLI coder (model = "provider/model"; required when selected live).</summary>
    [JsonPropertyName("opencode")]
    public CoderCliAgentConfig OpenCode { get; set; } = new();

    /// <summary>Settings for the Pi coding-agent CLI (model = "provider/id"; required when selected live).</summary>
    [JsonPropertyName("pi")]
    public CoderCliAgentConfig Pi { get; set; } = new();

    [JsonPropertyName("aider")]
    public AiderConfig Aider { get; set; } = new();

    [JsonPropertyName("build_command")]
    public string BuildCommand { get; set; } = "dotnet build";

    [JsonPropertyName("test_command")]
    public string TestCommand { get; set; } = "dotnet test";

    /// <summary>C# 14 field-backed properties: budgets are clamped on write (including during deserialization).</summary>
    [JsonPropertyName("max_attempts_before_escalation")]
    public int MaxAttemptsBeforeEscalation
    {
        get => field;
        set => field = Math.Max(1, value);
    } = 10;

    [JsonPropertyName("max_total_attempts")]
    public int MaxTotalAttempts
    {
        get => field;
        set => field = Math.Max(1, value);
    } = 20;

    [JsonPropertyName("mock")]
    public MockBehaviorConfig Mock { get; set; } = new();

    [JsonIgnore]
    public string NormalizedProviderMode => (ProviderMode ?? "").Trim().ToLowerInvariant() switch
    {
        "mock" => "mock",
        "aider" or "openai-compatible" => "aider",
        var value => throw new InvalidOperationException(
            $"unknown provider_mode '{value}' - supported: mock, aider."),
    };

    public void Validate()
    {
        _ = NormalizedProviderMode;
        if (LocalModels is null || Aider is null || OpenCode is null || Pi is null || Mock is null)
            throw new InvalidOperationException("config contains a null settings object.");
    }
}

public sealed class LocalModelsConfig
{
    [JsonPropertyName("coder")]
    public string Coder { get; set; } = "Qwen3.6-27B";

    [JsonPropertyName("reviewer")]
    public string Reviewer { get; set; } = "Devstral-24B";

    /// <summary>Optional dedicated endpoint for the coder. Empty falls back to local_models_endpoint.</summary>
    [JsonPropertyName("coder_endpoint")]
    public string CoderEndpoint { get; set; } = "";

    /// <summary>Optional dedicated endpoint for the reviewer. Empty falls back to local_models_endpoint.</summary>
    [JsonPropertyName("reviewer_endpoint")]
    public string ReviewerEndpoint { get; set; } = "";
}

/// <summary>Knobs used by the offline mock agents to simulate retry / escalation paths deterministically.</summary>
public sealed class MockBehaviorConfig
{
    [JsonPropertyName("reviewer_fail_attempts")]
    public int ReviewerFailAttempts { get; set; }

    [JsonPropertyName("tester_fail_attempts")]
    public int TesterFailAttempts { get; set; }

    /// <summary>When true the mock reviewer fails even after frontier advice, exercising the BLOCKED path.</summary>
    [JsonPropertyName("reviewer_ignores_advice")]
    public bool ReviewerIgnoresAdvice { get; set; }
}

/// <summary>Settings for a CLI coding agent (OpenCode, Pi).</summary>
public sealed class CoderCliAgentConfig
{
    /// <summary>Model string in the tool's own provider notation; live OpenCode/Pi require it.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>Extra command-line flags passed to every invocation.</summary>
    [JsonPropertyName("extra_args")]
    public string ExtraArgs { get; set; } = "";
}

/// <summary>Settings for the aider CLI, which is the default coding agent in live mode.</summary>
public sealed class AiderConfig
{
    /// <summary>Full aider model string (e.g. "openai/Qwen3.6-27B"). Empty derives openai/&lt;coder&gt;.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    /// <summary>Extra command-line flags passed to every aider invocation.</summary>
    [JsonPropertyName("extra_args")]
    public string ExtraArgs { get; set; } = "--no-auto-commits --yes-always --no-check-update";
}
