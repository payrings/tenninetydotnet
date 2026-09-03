using Tenninety.Core.Models;
using Tenninety.Execution.Aider;
using Tenninety.Execution.Coding;
using Tenninety.Execution.Mock;
using Tenninety.Execution.OpenAi;
using Tenninety.Execution.OpenCode;
using Tenninety.Execution.Pi;
using Tenninety.Execution.Reviewing;
using Tenninety.Execution.Testing;
using Tenninety.Git;

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
        if (IsMock) return;

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
    }

    private string CoderAgent => _config.CoderAgent.Trim().ToLowerInvariant();

    private static string StripPrefix(string model)
    {
        var slash = model.LastIndexOf('/');
        return slash >= 0 ? model[(slash + 1)..] : model;
    }

    /// <summary>Builds a Coder without any implicit host fallback. Docker mode requires the
    /// authoritative Git service and the orchestrator's already-held daemon-lock lease so the
    /// trusted gate can promote only after removal. Mock and explicit unsafe-host never touch
    /// Docker.</summary>
    public ICoderAgent CreateCoder(
        IGitService authoritativeGit,
        DaemonLockLease? lease = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(authoritativeGit);
        if (IsMock) return new MockCoderAgent(authoritativeGit.RepoPath);
        ValidateLiveConfiguration();
        if (CoderAgent is not ("aider" or "opencode" or "pi"))
            throw new NotSupportedException(
                $"unknown coder_agent '{CoderAgent}' - supported: aider, opencode, pi.");
        if (_config.Sandbox.NormalizedMode == "docker")
        {
            _config.Sandbox.ValidateLiveDocker();
            return new SandboxCoderGate(
                authoritativeGit,
                _config,
                lease ?? throw new InvalidOperationException(
                    "live Docker Coder construction requires the orchestrator's existing " +
                    "daemon-lock lease; a nested acquire or host fallback is forbidden."),
                log);
        }

        log?.Invoke("WARNING: sandbox.mode=unsafe-host; the Coder runs on the authoritative host checkout.");
        var timeout = TimeSpan.FromMinutes(Math.Max(1, _config.AttemptTimeoutMinutes));
        return CoderAgent switch
        {
            "aider" => new AiderCoderAgent(
                _config, EndpointFor("coder"), authoritativeGit.RepoPath),
            "opencode" => new OpenCode.OpenCodeCoderAgent(
                _config.OpenCode.Model, _config.OpenCode.ExtraArgs, timeout,
                authoritativeGit.RepoPath),
            "pi" => new Pi.PiCoderAgent(
                _config.Pi.Model, _config.Pi.ExtraArgs, timeout,
                authoritativeGit.RepoPath),
            var other => throw new NotSupportedException(
                $"unknown coder_agent '{other}' – supported: aider, opencode, pi."),
        };
    }

    public IReviewerAgent CreateReviewer(
        IGitService authoritativeGit, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(authoritativeGit);
        if (IsMock) return new MockReviewerAgent(_config.Mock.ReviewerFailAttempts, _config.Mock.ReviewerIgnoresAdvice);
        ValidateLiveConfiguration();
        var chat = new LocalChatClient(HttpClientFor(EndpointFor("reviewer")));
        var model = string.IsNullOrWhiteSpace(_config.LocalModels.Reviewer)
            ? "reviewer"
            : _config.LocalModels.Reviewer;
        if (_config.Sandbox.NormalizedMode == "docker")
        {
            _config.Sandbox.ValidateLiveDocker();
            return new SandboxReviewerGate(authoritativeGit, _config, chat, model, log);
        }
        log?.Invoke(
            "WARNING: sandbox.mode=unsafe-host; the Reviewer reads a host-derived patch.");
        return new OpenAiReviewerAgent(
            chat,
            model,
            _config.AttemptTimeoutMinutes,
            ctx => authoritativeGit.DiffPatchAgainstMain(ctx.Candidate.WorkBranch));
    }

    /// <summary>
    /// Builds the Tester implementation for the configured modes (fail closed):
    ///   - mock provider            → <see cref="Mock.MockTesterAgent"/> (no Docker, no shell);
    ///   - live docker mode         → <see cref="Testing.SandboxTesterGate"/> (offline Docker
    ///     sandbox; Docker resources are created lazily at run time only);
    ///   - explicit unsafe-host     → <see cref="Testing.UnsafeHostTesterAgent"/> (prominent warning).
    /// Unknown modes fail closed; unsafe-host is NEVER a fallback for Docker failures, failed
    /// preflight, invalid images, failed workspace creation, timeouts or enabled restore.
    /// Docker failures surface as failed gate results, never as host execution.
    /// Trusted callers supply the authoritative Git dependency; the Tester context never
    /// carries the repository path.
    /// </summary>
    public ITesterAgent CreateTester(IGitService authoritativeGit, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(authoritativeGit);

        // Mock selection must never touch Docker: no executable resolution, no settings, no
        // temporary directories — only the deterministic in-process implementation.
        if (IsMock) return new MockTesterAgent(_config.Mock.TesterFailAttempts);

        return _config.Sandbox.NormalizedMode switch
        {
            "unsafe-host" => new UnsafeHostTesterAgent(
                authoritativeGit,
                _config.TestCommand,
                _config.BuildCommand,
                TimeSpan.FromMinutes(Math.Max(1, _config.AttemptTimeoutMinutes) * 2),
                log),
            "docker" => CreateDockerTester(authoritativeGit, log),
            var mode => throw new InvalidOperationException(
                $"unknown sandbox mode '{mode}' for the Tester role; failing closed."),
        };
    }

    /// <summary>Live Docker configuration validation for the Tester path, then the offline
    /// gate. Construction is lazy: no Docker executable is resolved, no settings read and no
    /// temporary directory created until the gate actually runs.</summary>
    private ITesterAgent CreateDockerTester(IGitService authoritativeGit, Action<string>? log)
    {
        _config.Sandbox.ValidateLiveDocker();
        return new SandboxTesterGate(authoritativeGit, _config, log);
    }
}
