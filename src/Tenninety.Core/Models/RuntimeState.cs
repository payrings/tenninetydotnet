using System.Text.Json.Serialization;

namespace Tenninety.Core.Models;

/// <summary>Live runtime state persisted to .tenninety/state.json (Part III.3).</summary>
public sealed class RuntimeState
{
    [JsonPropertyName("current_wp")]
    public string? CurrentWp { get; set; }

    [JsonPropertyName("execution_mode")]
    public string ExecutionMode { get; set; } = "serial";

    [JsonPropertyName("attempts")]
    public Dictionary<string, AttemptInfo> Attempts { get; set; } = new();

    [JsonPropertyName("queue_status")]
    public Dictionary<string, string> QueueStatus { get; set; } = new();

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("stop_requested")]
    public bool StopRequested { get; set; }

    [JsonPropertyName("spec_hash")]
    public string? SpecHash { get; set; }

    [JsonPropertyName("sandbox_recovery")]
    public SandboxRecoveryInfo SandboxRecovery { get; set; } = new();
}

public sealed class SandboxRecoveryInfo
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "not-run";

    [JsonPropertyName("last_run_utc")]
    public string? LastRunUtc { get; set; }

    [JsonPropertyName("containers_found")]
    public int ContainersFound { get; set; }

    [JsonPropertyName("containers_removed")]
    public int ContainersRemoved { get; set; }

    [JsonPropertyName("workspaces_found")]
    public int WorkspacesFound { get; set; }

    [JsonPropertyName("workspaces_removed")]
    public int WorkspacesRemoved { get; set; }

    [JsonPropertyName("quarantined")]
    public List<string> Quarantined { get; set; } = new();

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "startup recovery has not run";
}

public sealed class AttemptInfo
{
    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; } = 10;

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("last_failure_type")]
    public string? LastFailureType { get; set; }

    [JsonPropertyName("last_failure_reasons")]
    public List<string> LastFailureReasons { get; set; } = new();

    [JsonPropertyName("frontier_advice_used")]
    public bool FrontierAdviceUsed { get; set; }

    [JsonPropertyName("feedback")]
    public List<string> Feedback { get; set; } = new();

    [JsonPropertyName("advice")]
    public List<string> Advice { get; set; } = new();
}
