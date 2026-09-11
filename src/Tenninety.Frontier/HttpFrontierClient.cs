using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Serialization;
using Tenninety.Core;
using Tenninety.Core.Models;
using Tenninety.Core.Security;
using Tenninety.Core.Stores;

namespace Tenninety.Frontier;

public sealed class FrontierCallException(string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>Bounded diagnostic: at most this many characters of a remote body or
    /// parser message ever appear in a FrontierCallException message.</summary>
    public const int MaxDiagnosticChars = 300;
}

/// <summary>OpenAI-compatible chat-completions client used to reach the Frontier Model (host-side only, Part VI.2).</summary>
public sealed class HttpFrontierClient : IFrontierClient
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxRepairJsonBytes = 128 * 1024;
    private readonly HttpClient _http;
    private readonly TenNinetyConfig _config;
    private readonly TimeSpan _requestTimeout;

    public HttpFrontierClient(HttpClient http, TenNinetyConfig config)
        : this(http, config, TimeSpan.FromMinutes(config.AttemptTimeoutMinutes))
    {
    }

    internal HttpFrontierClient(
        HttpClient http, TenNinetyConfig config, TimeSpan requestTimeout)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        if (requestTimeout <= TimeSpan.Zero || requestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout), "the Frontier request timeout must be finite and positive.");
        _requestTimeout = requestTimeout;
        // ResponseHeadersRead ends HttpClient's own timeout at header receipt. One linked
        // deadline below owns both headers and streaming-body reads instead.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        var key = Environment.GetEnvironmentVariable(config.FrontierApiKeyEnv);
        if (!string.IsNullOrEmpty(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("tenninety/1");
    }

    public Task<Plan> GeneratePlanAsync(string sanitizedSpecMarkdown, CancellationToken ct = default) =>
        CompleteAsync(
            Prompts.PlannerPrompt.System,
            Prompts.PlannerPrompt.BuildUserMessage(sanitizedSpecMarkdown),
            ParseAndValidatePlan,
            ct);

    public Task<RepairAdvice> GetRepairAdviceAsync(RepairRequest request, CancellationToken ct = default) =>
        CompleteAsync(
            Prompts.RepairPrompt.System,
            Prompts.RepairPrompt.BuildUserMessage(
                Core.Security.Sanitizer.SanitizeText(Json.Serialize(request.WorkPackage)), request.TotalAttempts,
                request.Feedback.Select(Sanitizer.SanitizeText).ToList(),
                Sanitizer.SanitizeText(request.PreviousAdvice ?? ""),
                Sanitizer.SanitizeText(request.RecentAuditTail),
                Sanitizer.SanitizeText(request.SanitizedDiff)),
            ParseAndValidateRepairAdvice,
            ct);

    public Task<PivotProposal> ProposePivotAsync(PivotRequest request, CancellationToken ct = default) =>
        CompleteAsync<PivotProposal>(Prompts.PivotPrompt.System,
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_requestTimeout);
        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var responseBody = await ReadBoundedAsync(response.Content, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new FrontierCallException(
                    $"frontier call failed ({(int)response.StatusCode}): " +
                    FrontierDiagnostics.Build(responseBody));

            ChatCompletionResponse? completion;
            try
            {
                completion = System.Text.Json.JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, Json.Options);
            }
            catch (System.Text.Json.JsonException ex)
            {
                // Remote-body diagnostics are centralized: sanitized AND bounded BEFORE they can
                // reach logs or the terminal — redaction first (so bounding cannot strip a
                // secret's identifying prefix while keeping its value), then control-character
                // stripping, then the bound.
                throw new FrontierCallException(
                    "frontier returned non-JSON body: " + FrontierDiagnostics.Build(responseBody), ex);
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
                throw new FrontierCallException(
                    "failed to parse frontier JSON response: " +
                    FrontierDiagnostics.Build(ex.Message), ex);
            }
        }
        catch (OperationCanceledException ex) when (
            deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new FrontierCallException(
                $"frontier call timed out after {_requestTimeout.TotalSeconds:0.###} seconds.", ex);
        }
    }

    /// <summary>Plan parsing at the untrusted Frontier boundary: STRICT ingestion (unknown and
    /// duplicate fields, depth and size bounds) followed by full blueprint validation. Every
    /// failure surfaces as a controlled <see cref="FrontierCallException"/> whose
    /// model-controlled text went through the shared <see cref="FrontierDiagnostics"/>
    /// construction — validation errors are sanitized and bounded exactly like HTTP bodies,
    /// non-JSON responses, parser messages and JSON extraction errors, regardless of which
    /// exception path produced them.</summary>
    private static Plan ParseAndValidatePlan(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        StrictJsonIngestion.EnsureStrictShape(bytes, StrictJsonIngestion.MaxPlanBytes, "frontier plan");
        var plan = StrictJsonIngestion.Deserialize<Plan>(bytes, "frontier plan");
        var validation = Tenninety.Core.Validation.PlanValidator.Validate(plan);
        if (!validation.IsValid)
            throw new FrontierCallException(
                "frontier produced an invalid plan: " +
                FrontierDiagnostics.BuildJoined("; ", validation.Errors));

        // Untrusted output: whatever statuses a model invents (DONE, BLOCKED…), every package
        // enters the queue as PENDING. The validator's warnings above still document what the
        // model tried to claim.
        foreach (var wp in plan.WorkPackages)
            wp.Status = TenNinety.WpStatus.Pending;
        return plan;
    }

    private static RepairAdvice ParseAndValidateRepairAdvice(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        StrictJsonIngestion.EnsureStrictShape(
            bytes, MaxRepairJsonBytes, "frontier repair advice");
        return RepairAdvice.ValidateAndCopy(
            StrictJsonIngestion.Deserialize<RepairAdvice>(
                bytes, "frontier repair advice"));
    }

    private static string JoinUrl(string baseUrl, string path) =>
        baseUrl.TrimEnd('/') + "/" + path.TrimStart('/');

    private static async Task<string> ReadBoundedAsync(
        HttpContent content, CancellationToken ct)
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
