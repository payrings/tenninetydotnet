using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Security;
using Tenninety.Core.Stores;

namespace Tenninety.Frontier;

public sealed class FrontierCallException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>OpenAI-compatible chat-completions client used to reach the Frontier Model (host-side only, Part VI.2).</summary>
public sealed class HttpFrontierClient : IFrontierClient
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly TenNinetyConfig _config;

    public HttpFrontierClient(HttpClient http, TenNinetyConfig config)
    {
        _http = http;
        _config = config;
        var key = Environment.GetEnvironmentVariable(config.FrontierApiKeyEnv);
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("tenninety/3.2");
    }

    public Task<Plan> GeneratePlanAsync(string sanitizedSpecMarkdown, CancellationToken ct = default) =>
        CompleteAsync(
            Prompts.PlannerPrompt.System,
            Prompts.PlannerPrompt.BuildUserMessage(sanitizedSpecMarkdown),
            ParseAndValidatePlan,
            ct);

    public Task<RepairAdvice> GetRepairAdviceAsync(RepairRequest request, CancellationToken ct = default) =>
        CompleteAsync<RepairAdvice>(
            Prompts.RepairPrompt.System,
            Prompts.RepairPrompt.BuildUserMessage(
                Core.Security.Sanitizer.SanitizeText(Json.Serialize(request.WorkPackage)), request.TotalAttempts,
                request.Feedback.Select(Sanitizer.SanitizeText).ToList(),
                Sanitizer.SanitizeText(request.PreviousAdvice ?? ""),
                Sanitizer.SanitizeText(request.RecentAuditTail),
                Sanitizer.SanitizeText(request.SanitizedDiff)),
            ct);

    public Task<PivotProposal> ProposePivotAsync(PivotRequest request, CancellationToken ct = default) =>
        CompleteAsync<PivotProposal>(            Prompts.PivotPrompt.System,
            Prompts.PivotPrompt.BuildUserMessage(
                Sanitizer.SanitizeText(request.SpecSnapshot),
                Sanitizer.SanitizeText(request.PlanJson),
                Sanitizer.SanitizeText(request.UserIntent),
                Sanitizer.SanitizeText(request.AuditTail)),
            ct);

    public Task<RevertGuidance> ProposeRevertAsync(RevertRequest request, CancellationToken ct = default) =>
        CompleteAsync<RevertGuidance>(
            Prompts.RevertPrompt.System,
            Prompts.RevertPrompt.BuildUserMessage(
                Sanitizer.SanitizeText(request.CommitInfo),
                Sanitizer.SanitizeText(request.SanitizedDiff),
                Sanitizer.SanitizeText(request.Reason)),
            ct);

    private Task<T> CompleteAsync<T>(string system, string user, CancellationToken ct) where T : class =>
        CompleteAsync(system, user, Json.Deserialize<T>, ct);

    private async Task<T> CompleteAsync<T>(
        string system, string user, Func<string, T> parse, CancellationToken ct) where T : class
    {
        var payload = new ChatCompletionRequest(
            Model: _config.FrontierModel,
            Messages:
            [
                new ChatMessage("system", system),
                new ChatMessage("user", user),
            ],
            Temperature: 0.2);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, JoinUrl(_config.FrontierEndpoint, "chat/completions"))
        {
            Content = new StringContent(Json.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var responseBody = await ReadBoundedAsync(response.Content, ct);
        if (!response.IsSuccessStatusCode)
            throw new FrontierCallException(
                $"frontier call failed ({(int)response.StatusCode}): " +
                Truncate(Sanitizer.SanitizeText(responseBody)));

        ChatCompletionResponse? completion;
        try
        {
            completion = System.Text.Json.JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, Json.Options);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FrontierCallException($"frontier returned non-JSON body: {Truncate(responseBody)}", ex);
        }
        if (completion is null)
            throw new FrontierCallException("frontier returned an empty completion.");
        var messageContent = completion.Choices.FirstOrDefault()?.Message.Content
            ?? throw new FrontierCallException("frontier completion had no message content.");

        try
        {
            return parse(JsonExtractor.ExtractFirstJsonObject(messageContent));
        }
        catch (Exception ex) when (ex is not FrontierCallException)
        {
            throw new FrontierCallException($"failed to parse frontier JSON response: {ex.Message}", ex);
        }
    }

    private static Plan ParseAndValidatePlan(string json)
    {
        var plan = Json.Deserialize<Plan>(json);
        var validation = Tenninety.Core.Validation.PlanValidator.Validate(plan);
        if (!validation.IsValid)
            throw new FrontierCallException(
                "frontier produced an invalid plan: " + string.Join("; ", validation.Errors));

        // Untrusted output: whatever statuses a model invents (DONE, BLOCKED…), every package
        // enters the queue as PENDING. The validator's warnings above still document what the
        // model tried to claim.
        foreach (var wp in plan.WorkPackages)
            wp.Status = TenNinety.WpStatus.Pending;
        return plan;
    }

    private static string JoinUrl(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes)
            throw new FrontierCallException(
                $"frontier response exceeded the {MaxResponseBytes / 1024 / 1024} MiB limit.");

        await using var input = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (output.Length + read > MaxResponseBytes)
                throw new FrontierCallException(
                    $"frontier response exceeded the {MaxResponseBytes / 1024 / 1024} MiB limit.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    internal sealed record ChatCompletionRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] ChatMessage[] Messages,
        [property: JsonPropertyName("temperature")] double Temperature);

    internal sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    internal sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice> Choices { get; set; } = new();

        public sealed class Choice
        {
            [JsonPropertyName("message")]
            public ChatMessage Message { get; set; } = new("assistant", "");
        }
    }
}
