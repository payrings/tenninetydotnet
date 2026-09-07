namespace Tenninety.Core.Models;

/// <summary>
/// Single trusted point for model-endpoint selection. Every consumer - the host-side agents
/// (via <c>AgentFactory.EndpointFor</c>) and the container-side coder plan
/// (<c>CoderToolPlan.Create</c>) - resolves endpoints here so the llama-swap versus
/// direct-model decision cannot drift between unrelated classes.
///
/// Two networking contexts exist and must never be confused:
///  - HOST: the model server as reached from host processes (Reviewer, aider under
///    unsafe-host). Host loopback is legitimate here.
///  - CODER SANDBOX: the model server as reached from INSIDE the disposable Coder container.
///    Container loopback would refer to the container itself, so self-referencing hosts are
///    rejected in this context - exactly like <see cref="SandboxConfig.ValidateLiveDocker"/>.
///
/// With <c>use_llama_swap</c> enabled, coder and reviewer route through one llama-swap proxy
/// (which hot-swaps the resident model by identifier): the host roles use
/// <c>llama_swap_endpoint</c> and the sandboxed Coder uses <c>llama_swap_coder_endpoint</c>.
/// With llama-swap disabled, the previous behavior is preserved unchanged: the sandboxed Coder
/// keeps using <c>sandbox.roles.coder.model_endpoint</c> and host roles keep the
/// shared/per-role endpoint fallback.
/// </summary>
public static class ModelEndpointResolver
{
    /// <summary>Host-side endpoint for a role: llama-swap overrides everything; otherwise a
    /// per-role endpoint wins over the shared one. Trailing slash guaranteed so HttpClient
    /// relative calls keep the /v1 prefix. Loopback addresses are allowed: the host process
    /// legitimately reaches a model server on 127.0.0.1.</summary>
    public static string ResolveHostEndpoint(TenNinetyConfig config, string role)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.UseLlamaSwap)
            return WithTrailingSlash(ValidateHttpEndpoint(
                config.LlamaSwapEndpoint, "llama_swap_endpoint", containerPerspective: false));
        var perRole = role.Equals("coder", StringComparison.OrdinalIgnoreCase)
            ? config.LocalModels.CoderEndpoint
            : config.LocalModels.ReviewerEndpoint;
        return WithTrailingSlash(ValidateHttpEndpoint(
            perRole,
            string.IsNullOrWhiteSpace(perRole)
                ? "local_models_endpoint"
                : role.Equals("coder", StringComparison.OrdinalIgnoreCase)
                    ? "local_models.coder_endpoint"
                    : "local_models.reviewer_endpoint",
            containerPerspective: false,
            fallback: config.LocalModelsEndpoint));
    }

    /// <summary>OpenAI-compatible endpoint as seen FROM INSIDE the disposable Coder container:
    /// the llama-swap address when llama-swap is enabled, otherwise the explicit sandbox role
    /// endpoint. Trailing slashes are stripped (aider's --openai-api-base and the container
    /// environment variables carry the bare base URL). Malformed, credential-bearing or
    /// self-referencing values fail closed with the owning configuration field named.</summary>
    public static string ResolveCoderContainerEndpoint(TenNinetyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.UseLlamaSwap
            ? ValidateHttpEndpoint(
                config.LlamaSwapCoderEndpoint, "llama_swap_coder_endpoint",
                containerPerspective: true).TrimEnd('/')
            : ValidateHttpEndpoint(
                config.Sandbox.Roles.Coder.ModelEndpoint, "sandbox.roles.coder.model_endpoint",
                containerPerspective: true).TrimEnd('/');
    }

    /// <summary>Validates an http(s) endpoint: absolute http(s) URL with no embedded
    /// user-information credentials. In container perspective, loopback IPv4/IPv6 forms
    /// (127.0.0.0/8, ::1, …), the unspecified addresses, and localhost in any case or
    /// trailing-dot form are also rejected. Returns the trimmed value. Error messages never
    /// echo the raw value except for the self-reference case, matching the long-standing
    /// SandboxConfig behavior.</summary>
    public static string ValidateHttpEndpoint(
        string? endpoint, string field, bool containerPerspective, string? fallback = null)
    {
        var candidate = string.IsNullOrWhiteSpace(endpoint) ? fallback : endpoint;
        if (string.IsNullOrWhiteSpace(candidate) ||
            !Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"{field} must be an absolute http(s) URL " +
                (containerPerspective
                    ? "that the container can reach, e.g. http://coder-model:8000/v1, "
                    : "reachable from the host process, e.g. http://127.0.0.1:8080/v1, ") +
                "but the configured value has a missing or unsupported scheme " +
                "(value withheld to avoid echoing untrusted input).");
        if (uri.UserInfo.Length > 0)
            throw new InvalidOperationException(
                $"{field} must not embed user-information credentials " +
                "(user:password@…) in the URL. Pass any token out-of-band, never in the URL.");
        if (containerPerspective && SandboxConfig.IsSelfReferencingHost(uri.Host))
            throw new InvalidOperationException(
                $"{field} '{endpoint}' refers to the container itself: loopback addresses " +
                "(127.0.0.0/8, ::1, …) and localhost inside a container never reach the host " +
                "or the model. Serve the model on the model network (e.g. " +
                "http://coder-model:8000/v1) or behind an explicitly Docker-reachable proxy.");
        return candidate.Trim();
    }

    private static string WithTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}
