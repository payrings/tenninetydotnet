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

    /// <summary>Per-attempt wall-clock budget for one live agent call, in minutes. Explicit
    /// values below 1 (or above the persisted bound) are rejected with a named-field
    /// validation error — never silently clamped; an omitted value keeps the default.</summary>
    [JsonPropertyName("attempt_timeout_minutes")]
    public int AttemptTimeoutMinutes { get; set; } = 10;

    /// <summary>Human-settable switch: route both local models through one llama-swap proxy so
    /// the coder and reviewer can share one GPU card (models are swapped on demand by name).
    /// When true, host roles use <see cref="LlamaSwapEndpoint"/> instead of
    /// <see cref="LocalModelsEndpoint"/>, and the sandboxed Coder uses
    /// <see cref="LlamaSwapCoderEndpoint"/> instead of <c>sandbox.roles.coder.model_endpoint</c>.</summary>
    [JsonPropertyName("use_llama_swap")]
    public bool UseLlamaSwap { get; set; }

    /// <summary>Base URL of the OpenAI-compatible endpoint serving the local coder/reviewer
    /// models as reached from HOST processes (Reviewer, aider under unsafe-host).</summary>
    [JsonPropertyName("llama_swap_endpoint")]
    public string LlamaSwapEndpoint { get; set; } = "http://localhost:8080/v1";

    /// <summary>
    /// llama-swap endpoint as reached FROM INSIDE the disposable Coder container (Docker
    /// network DNS, never host loopback). Used only while <see cref="UseLlamaSwap"/> is set
    /// and only in Docker mode; with llama-swap disabled the sandbox keeps its explicit
    /// <c>sandbox.roles.coder.model_endpoint</c>. Resolution and validation are centralized
    /// in <see cref="ModelEndpointResolver"/>.
    /// </summary>
    [JsonPropertyName("llama_swap_coder_endpoint")]
    public string LlamaSwapCoderEndpoint { get; set; } = "http://llama-swap:8080/v1";

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

    /// <summary>Explicit values outside [1, 1000] are rejected with a named-field validation
    /// error — never silently clamped; an omitted value keeps the default. MUST be strictly
    /// less than <see cref="MaxTotalAttempts"/> (see <see cref="ValidateRetryThresholds"/>).</summary>
    [JsonPropertyName("max_attempts_before_escalation")]
    public int MaxAttemptsBeforeEscalation { get; set; } = 10;

    /// <summary>Explicit values outside [1, 10000] are rejected with a named-field validation
    /// error — never silently clamped; an omitted value keeps the default.</summary>
    [JsonPropertyName("max_total_attempts")]
    public int MaxTotalAttempts { get; set; } = 20;

    [JsonPropertyName("mock")]
    public MockBehaviorConfig Mock { get; set; } = new();

    /// <summary>
    /// Container-isolation contract. Missing section deserializes to the defaults, i.e. docker
    /// mode – missing sandbox configuration can never silently select host execution. Structural
    /// validation runs on load; live image and endpoint requirements run when Docker execution is
    /// selected.
    /// </summary>
    [JsonPropertyName("sandbox")]
    public SandboxConfig Sandbox { get; set; } = new();

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
        if (LocalModels is null || Aider is null || OpenCode is null || Pi is null || Mock is null)
            throw new InvalidOperationException("config contains a null settings object.");
        if (Sandbox is null)
            throw new InvalidOperationException("config contains a null sandbox settings object.");
        // Null-string validation runs FIRST: a JSON "field": null must be reported as a null
        // value with its owning field name, not as a downstream "unknown mode" style error.
        ValidateRequiredStrings();
        _ = NormalizedProviderMode;
        Sandbox.ValidateStructural();
        ValidateRetryThresholds();
        ValidateBounds();
    }

    /// <summary>Explicit null/blank rejection for string-bearing configuration. A JSON
    /// `"field": null` must never silently degrade into the empty-string default, so every
    /// security- or behavior-relevant string is checked with its owning field name.</summary>
    private void ValidateRequiredStrings()
    {
        RequireNonNullOrThrow(ExecutionMode, "execution_mode");
        RequireNonNullOrThrow(FrontierEndpoint, "frontier_endpoint");
        RequireNonNullOrThrow(ProviderMode, "provider_mode");
        RequireNonNullOrThrow(FrontierModel, "frontier_model");
        RequireNonNullOrThrow(FrontierApiKeyEnv, "frontier_api_key_env");
        RequireNonNullOrThrow(LocalModelsEndpoint, "local_models_endpoint");
        RequireNonNullOrThrow(LlamaSwapEndpoint, "llama_swap_endpoint");
        RequireNonNullOrThrow(LlamaSwapCoderEndpoint, "llama_swap_coder_endpoint");
        RequireNonNullOrThrow(CoderAgent, "coder_agent");
        RequireNonNullOrThrow(BuildCommand, "build_command");
        RequireNonNullOrThrow(TestCommand, "test_command");
        if (LocalModels is not null)
        {
            RequireNonNullOrThrow(LocalModels.Coder, "local_models.coder");
            RequireNonNullOrThrow(LocalModels.Reviewer, "local_models.reviewer");
            RequireNonNullOrThrow(LocalModels.CoderEndpoint, "local_models.coder_endpoint");
            RequireNonNullOrThrow(LocalModels.ReviewerEndpoint, "local_models.reviewer_endpoint");
        }
        if (Aider is not null)
        {
            RequireNonNullOrThrow(Aider.Model, "aider.model");
            RequireNonNullOrThrow(Aider.ExtraArgs, "aider.extra_args");
        }
        if (OpenCode is not null)
        {
            RequireNonNullOrThrow(OpenCode.Model, "opencode.model");
            RequireNonNullOrThrow(OpenCode.ExtraArgs, "opencode.extra_args");
        }
        if (Pi is not null)
        {
            RequireNonNullOrThrow(Pi.Model, "pi.model");
            RequireNonNullOrThrow(Pi.ExtraArgs, "pi.extra_args");
        }
    }

    private static void RequireNonNullOrThrow(string? value, string field)
    {
        if (value is null)
            throw new InvalidOperationException(
                $"config field '{field}' must not be null: set it to a string value or omit " +
                "the field entirely to receive its default.");
    }

    /// <summary>
    /// The engine checks the TOTAL attempt budget BEFORE escalating to the Frontier
    /// (ExecutionEngine.HandleThresholdAsync), so the local budget must be strictly smaller
    /// than the total budget — equality or reversal would make Frontier advice unreachable.
    /// Both bounds are also capped so a typo cannot disable the gate indefinitely.
    /// </summary>
    private void ValidateRetryThresholds()
    {
        // Field-range bounds run FIRST so an out-of-range value is reported by its OWN field
        // (never silently clamped), then the cross-field relationship.
        if (MaxAttemptsBeforeEscalation is < 1 or > 1_000)
            throw new InvalidOperationException(
                $"max_attempts_before_escalation must be within [1, 1000] but is " +
                $"{MaxAttemptsBeforeEscalation}.");
        if (MaxTotalAttempts is < 1 or > 10_000)
            throw new InvalidOperationException(
                $"max_total_attempts must be within [1, 10000] but is {MaxTotalAttempts}.");
        if (MaxAttemptsBeforeEscalation >= MaxTotalAttempts)
            throw new InvalidOperationException(
                $"max_attempts_before_escalation ({MaxAttemptsBeforeEscalation}) must be strictly " +
                $"less than max_total_attempts ({MaxTotalAttempts}): the engine blocks on the " +
                "total budget before escalating, so equal or reversed values would prevent " +
                "every Frontier repair-advice call.");
    }

    /// <summary>Upper bounds for the remaining numeric knobs. Explicit out-of-range values
    /// are rejected with the owning field named; an omitted value keeps its documented
    /// default. There is no silent clamping anywhere in configuration ingestion.</summary>
    private void ValidateBounds()
    {
        if (MaxConcurrentWorkers is < 1 or > 64)
            throw new InvalidOperationException(
                $"max_concurrent_workers must be within [1, 64] but is {MaxConcurrentWorkers}.");
        if (AttemptTimeoutMinutes is < 1 or > 10_080)
            throw new InvalidOperationException(
                $"attempt_timeout_minutes must be within [1, 10080] but is {AttemptTimeoutMinutes}.");
    }
}

public sealed class LocalModelsConfig
{
    /// <summary>Coder model identifier. Must match the llama-swap profile name (or the model
    /// served by the direct endpoint) and must differ from <see cref="Reviewer"/>.</summary>
    [JsonPropertyName("coder")]
    public string Coder { get; set; } = "coder";

    /// <summary>Reviewer model identifier. Must match the llama-swap profile name (or the
    /// model served by the direct endpoint) and must differ from <see cref="Coder"/>.</summary>
    [JsonPropertyName("reviewer")]
    public string Reviewer { get; set; } = "reviewer";

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
