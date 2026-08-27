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
