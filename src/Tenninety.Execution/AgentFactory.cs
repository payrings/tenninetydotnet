using Tenninety.Core.Models;
using Tenninety.Execution.Aider;
using Tenninety.Execution.Mock;
using Tenninety.Execution.OpenAi;
using Tenninety.Execution.OpenCode;
using Tenninety.Execution.Pi;

namespace Tenninety.Execution;

/// <summary>
/// Builds the local executor trio from config.
///
/// Two invariants (Part I of the framework: independent peer review):
///  1. The Coder and Reviewer must have different configured identifiers. Operators remain
///     responsible for ensuring those identifiers resolve to genuinely different weights.
///  2. When both models do not fit one GPU card, the human sets use_llama_swap=true so
///     both route through a llama-swap proxy, which swaps them on the card by name.
///
/// The coding agent itself is pluggable: aider (default), OpenCode or Pi – selected with
/// the coder_agent config knob. Review is always a direct chat call to the reviewer model,
/// and the mechanical test suite gates every promotion regardless of which agent typed.
/// </summary>
public sealed class AgentFactory
{
    private readonly TenNinetyConfig _config;
    private readonly string _providerMode;
    private bool _distinctModelsValidated;

    public AgentFactory(TenNinetyConfig config)
    {
        config.Validate();
        _config = config;
        _providerMode = config.NormalizedProviderMode;
    }

    public bool IsMock => _providerMode == "mock";

    /// <summary>Endpoint for a given role: llama-swap overrides everything; otherwise a
    /// per-role endpoint wins over the shared one. Trailing slash guaranteed so HttpClient
    /// relative calls keep the /v1 prefix.</summary>
    public string EndpointFor(string role)
    {
        if (_config.UseLlamaSwap) return WithTrailingSlash(_config.LlamaSwapEndpoint);
        var perRole = role.Equals("coder", StringComparison.OrdinalIgnoreCase)
            ? _config.LocalModels.CoderEndpoint
            : _config.LocalModels.ReviewerEndpoint;
        return WithTrailingSlash(
            string.IsNullOrWhiteSpace(perRole) ? _config.LocalModelsEndpoint : perRole);
    }

    private static string WithTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";

    /// <summary>Shared, cached HTTP client per base address (one per process, not per WP).</summary>
    public HttpClient HttpClientFor(string baseUrl)
    {
        var key = WithTrailingSlash(baseUrl);
        return SharedClients.GetOrAdd(key, url =>
        {
            var client = new HttpClient { BaseAddress = new Uri(url) };
            var key2 = Environment.GetEnvironmentVariable("TENNINETY_LOCAL_API_KEY");
            if (!string.IsNullOrEmpty(key2))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key2);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tenninety-local/3.2");
            return client;
        });
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HttpClient>
        SharedClients = new();

    private void ValidateLiveConfiguration()
    {
        if (IsMock || _distinctModelsValidated) return;

        if (CoderAgent == "opencode" && string.IsNullOrWhiteSpace(_config.OpenCode.Model) ||
            CoderAgent == "pi" && string.IsNullOrWhiteSpace(_config.Pi.Model))
            throw new InvalidOperationException(
                $"{CoderAgent}.model must be explicit in live mode so distinct coder/reviewer " +
                "identifiers can be enforced.");

        // Effective coder identity honours per-agent model overrides (aider/opencode/pi),
        // so an override that happens to equal the reviewer is caught just like the default.
        var coderIdentity = CoderAgent switch
        {
            "aider" when _config.Aider.Model.Length > 0 => StripPrefix(_config.Aider.Model),
            "opencode" when _config.OpenCode.Model.Length > 0 =>
                _config.OpenCode.Model.Split('/')[^1],
            "pi" when _config.Pi.Model.Length > 0 =>
                _config.Pi.Model.Split('/')[^1],
            _ => StripPrefix(_config.LocalModels.Coder),
        };
        var reviewer = StripPrefix(_config.LocalModels.Reviewer);
        if (string.IsNullOrWhiteSpace(coderIdentity) || string.IsNullOrWhiteSpace(reviewer))
            throw new InvalidOperationException(
                "both a coder and a reviewer model must be configured (.tenninety/config.json → local_models).");
        if (string.Equals(coderIdentity, reviewer, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "the coder and reviewer identifiers must differ " +
                $"(both resolve to '{coderIdentity}'). Configure genuinely different models for independent review.");

        _distinctModelsValidated = true;
    }

    private string CoderAgent => _config.CoderAgent.Trim().ToLowerInvariant();

    private static string StripPrefix(string model)
    {
        var slash = model.LastIndexOf('/');
        return slash >= 0 ? model[(slash + 1)..] : model;
    }

    public ICoderAgent CreateCoder()
    {
        if (IsMock) return new MockCoderAgent();
        ValidateLiveConfiguration();
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _config.AttemptTimeoutMinutes));
        return CoderAgent switch
        {
            "aider" => new AiderCoderAgent(_config, EndpointFor("coder")),
            "opencode" => new OpenCode.OpenCodeCoderAgent(
                _config.OpenCode.Model, _config.OpenCode.ExtraArgs, timeout),
            "pi" => new Pi.PiCoderAgent(
                _config.Pi.Model, _config.Pi.ExtraArgs, timeout),
            var other => throw new NotSupportedException(
                $"unknown coder_agent '{other}' – supported: aider, opencode, pi."),
        };
    }

    public IReviewerAgent CreateReviewer()
    {
        if (IsMock) return new MockReviewerAgent(_config.Mock.ReviewerFailAttempts, _config.Mock.ReviewerIgnoresAdvice);
        ValidateLiveConfiguration();
        return new OpenAiReviewerAgent(
            new LocalChatClient(HttpClientFor(EndpointFor("reviewer"))),
            string.IsNullOrWhiteSpace(_config.LocalModels.Reviewer) ? "reviewer" : _config.LocalModels.Reviewer,
            _config.AttemptTimeoutMinutes);
    }

    public ITesterAgent CreateTester(Action<string>? log = null) =>
        new ShellTesterAgent(
            _config.TestCommand,
            IsMock ? _config.Mock.TesterFailAttempts : 0,
            log,
            failWhenNoProject: !IsMock,
            buildCommand: IsMock ? "" : _config.BuildCommand,
            attemptTimeout: TimeSpan.FromMinutes(Math.Max(1, _config.AttemptTimeoutMinutes) * 2));
}
