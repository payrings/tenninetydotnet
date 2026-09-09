using System.Collections.ObjectModel;
using System.Text.Json;
using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Execution.Coding;

/// <summary>
/// Frozen container-side invocation for one supported coding tool. The executable path and all
/// security-relevant flags are selected by trusted code. Docker mode rejects configured extra
/// arguments because aliases and future tool options cannot be safely denylisted.
///
/// Pi contract: the pinned Pi container has an EMPTY tmpfs home and no provider configuration,
/// and Pi does not honor the generic OpenAI environment variables. Pi's supported custom
/// provider mechanism is <c>~/.pi/agent/models.json</c>, so for <c>coder_agent=pi</c> trusted
/// code generates that provider/model configuration (pointing at Tenninety's EFFECTIVE
/// container-side model endpoint) and supplies it as <see cref="HomeSetupCommand"/> — the gate
/// writes it into the bounded tmpfs HOME through a stdin-fed exec before the tool runs. The
/// API key never enters the file: Pi resolves <c>$OPENAI_API_KEY</c> from the (closed)
/// container environment at request time. OpenCode similarly receives a trusted inline
/// provider document whose API key is an environment reference. No credential and no host
/// configuration is baked into the image.
/// </summary>
public sealed record CoderToolPlan(
    string Tool,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    SandboxCommand? HomeSetupCommand = null)
{
    private const int MaxInstructionChars = 131_072;
    private const int MaxExtraArguments = 128;
    private const int MaxExtraArgumentChars = 4096;

    /// <summary>The pinned Pi image resolves its configuration under the bounded tmpfs HOME
    /// (<see cref="SandboxPolicy.ContainerHomePath"/>); these are the EXACT in-container paths
    /// of Pi's supported custom-provider file (Pi 0.85: <c>~/.pi/agent/models.json</c>).</summary>
    public const string PiAgentContainerDir = SandboxPolicy.ContainerHomePath + "/.pi/agent";
    public const string PiModelsContainerPath = PiAgentContainerDir + "/models.json";

    /// <summary>Fixed provider name used only when the configured Pi model carries no
    /// "provider/" prefix (Pi's documented model notation is "provider/id").</summary>
    public const string PiDefaultProviderName = "tenninety-local";

    public static CoderToolPlan Create(TenNinetyConfig config, CoderRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Validate();

        var instruction = CliCoderAgentBase.BuildInstruction(ctx);
        if (instruction.Length > MaxInstructionChars)
            throw new InvalidOperationException(
                "the coder instruction exceeds the bounded container invocation limit.");

        // Container-perspective endpoint: llama-swap when enabled, otherwise the explicit
        // sandbox role endpoint. Selection and fail-closed validation are centralized in
        // ModelEndpointResolver; loopback can never reach the model from inside the container.
        var endpoint = ModelEndpointResolver.ResolveCoderContainerEndpoint(config);
        IReadOnlyDictionary<string, string> environment = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OPENAI_API_KEY"] =
                    System.Environment.GetEnvironmentVariable("TENNINETY_LOCAL_API_KEY") ?? "dummy",
                ["OPENAI_BASE_URL"] = endpoint,
                ["OPENAI_API_BASE"] = endpoint,
            });

        return config.CoderAgent.Trim().ToLowerInvariant() switch
        {
            "aider" => BuildAider(config, instruction, endpoint, environment),
            "opencode" => BuildOpenCode(config, instruction, endpoint, environment),
            "pi" => BuildPi(config, instruction, endpoint, environment),
            var value => throw new NotSupportedException(
                $"unknown coder_agent '{value}' - supported: aider, opencode, pi."),
        };
    }

    public SandboxCommand ToSandboxCommand(TimeSpan timeout) => new()
    {
        Executable = Executable,
        Arguments = Arguments,
        WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
        Timeout = timeout,
        MaxOutputBytes = 4L * 1024 * 1024,
    };

    private static CoderToolPlan BuildAider(
        TenNinetyConfig config, string instruction, string endpoint,
        IReadOnlyDictionary<string, string> environment)
    {
        var model = string.IsNullOrWhiteSpace(config.Aider.Model)
            ? $"openai/{config.LocalModels.Coder}"
            : config.Aider.Model;
        var args = new List<string>
        {
            "--model", model,
            "--openai-api-base", endpoint,
            "--config", "/dev/null",
            "--env-file", "/dev/null",
            "--no-auto-commits",
            "--yes-always",
            "--no-check-update",
            "--message", instruction,
        };
        RejectExtraArguments(config.Aider.ExtraArgs, "aider",
            "--no-auto-commits", "--yes-always", "--no-check-update");
        return new CoderToolPlan(
            "aider", "/usr/local/bin/aider", args.AsReadOnly(), environment);
    }

    private static CoderToolPlan BuildOpenCode(
        TenNinetyConfig config, string instruction, string endpoint,
        IReadOnlyDictionary<string, string> environment)
    {
        if (string.IsNullOrWhiteSpace(config.OpenCode.Model))
            throw new InvalidOperationException(
                "opencode.model must be explicit for a containerized coder.");
        var (providerId, modelId) = SplitOpenCodeModel(config.OpenCode.Model);
        var openCodeEnvironment = new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(environment, StringComparer.Ordinal)
            {
                ["OPENCODE_CONFIG_CONTENT"] = BuildOpenCodeConfig(providerId, modelId, endpoint),
            });
        var args = new List<string>
        {
            "run", "--auto", "--model", config.OpenCode.Model, instruction,
        };
        RejectExtraArguments(config.OpenCode.ExtraArgs, "opencode");
        return new CoderToolPlan(
            "opencode", "/usr/local/bin/opencode", args.AsReadOnly(), openCodeEnvironment);
    }

    internal static (string ProviderId, string ModelId) SplitOpenCodeModel(string model)
    {
        var separator = model.IndexOf('/');
        if (model.Length > 512 || separator <= 0 || separator != model.LastIndexOf('/') ||
            separator == model.Length - 1 || model.Any(char.IsWhiteSpace) ||
            model.Any(char.IsControl))
            throw new InvalidOperationException(
                "opencode.model must be a bounded 'provider/model' identifier with exactly one '/'.");
        return (model[..separator], model[(separator + 1)..]);
    }

    internal static string BuildOpenCodeConfig(string providerId, string modelId, string endpoint)
    {
        var document = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["provider"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [providerId] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["npm"] = "@ai-sdk/openai-compatible",
                    ["name"] = "Tenninety local model",
                    ["options"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["baseURL"] = endpoint,
                        ["apiKey"] = "{env:OPENAI_API_KEY}",
                    },
                    ["models"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        [modelId] = new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["name"] = modelId,
                        },
                    },
                },
            },
        };
        var json = JsonSerializer.Serialize(document);
        if (json.Length > SandboxPolicy.MaxEnvironmentValueLength)
            throw new InvalidOperationException(
                "the generated OpenCode provider configuration exceeded its environment bound.");
        return json;
    }

    private static CoderToolPlan BuildPi(
        TenNinetyConfig config, string instruction, string endpoint,
        IReadOnlyDictionary<string, string> environment)
    {
        if (string.IsNullOrWhiteSpace(config.Pi.Model))
            throw new InvalidOperationException(
                "pi.model must be explicit for a containerized coder.");
        if (config.Pi.Model.Length > 512 || config.Pi.Model.Any(char.IsControl))
            throw new InvalidOperationException(
                "pi.model must be a bounded 'provider/id' string without control characters.");

        var (provider, modelId) = SplitPiModel(config.Pi.Model);
        var args = new List<string>
        {
            "-p", "--no-session",
            // Pinned container: Pi must not attempt update checks, package updates or
            // install/update telemetry at startup (--offline is Pi's documented equivalent
            // of PI_OFFLINE=1), and project-local extensions/skills from the untrusted
            // candidate workspace are ignored for the run.
            "--offline",
            "--no-approve",
            "--model", config.Pi.Model,
            instruction,
        };
        RejectExtraArguments(config.Pi.ExtraArgs, "pi");

        var homeSetup = new SandboxCommand
        {
            Executable = "/bin/sh",
            Arguments =
            [
                "-c",
                "mkdir -p '" + PiAgentContainerDir + "' && cat > '" + PiModelsContainerPath + "'",
            ],
            StdIn = BuildPiModelsJson(provider, modelId, endpoint),
            WorkingDirectory = SandboxPolicy.ContainerWorkspacePath,
            MaxOutputBytes = 65536,
        };
        return new CoderToolPlan(
            "pi", "/usr/local/bin/pi", args.AsReadOnly(), environment, homeSetup);
    }

    /// <summary>Splits Pi's documented "provider/id" model notation. A value without a
    /// slash selects the fixed local provider name; everything after the FIRST slash is the
    /// model id (provider ids themselves never contain slashes).</summary>
    internal static (string Provider, string ModelId) SplitPiModel(string model)
    {
        var trimmed = model.Trim();
        var slash = trimmed.IndexOf('/');
        return slash > 0
            ? (trimmed[..slash], trimmed[(slash + 1)..])
            : (PiDefaultProviderName, trimmed);
    }

    /// <summary>Deterministic Pi custom-provider configuration (models.json schema of the
    /// pinned Pi 0.85): one OpenAI-compatible provider named <paramref name="provider"/>
    /// whose baseUrl is Tenninety's EFFECTIVE container-side model endpoint and whose single
    /// model entry is <paramref name="modelId"/>. The apiKey uses Pi's documented
    /// <c>$ENV_VAR</c> resolution so the credential never enters the generated file.</summary>
    internal static string BuildPiModelsJson(string provider, string modelId, string endpoint)
    {
        var document = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["providers"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [provider] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["baseUrl"] = endpoint,
                    ["api"] = "openai-completions",
                    ["apiKey"] = "$OPENAI_API_KEY",
                    // Safe defaults for local OpenAI-compatible servers (llama-swap or a
                    // direct model server): no developer role, no reasoning_effort, no
                    // streamed-usage request.
                    ["compat"] = new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["supportsDeveloperRole"] = false,
                        ["supportsReasoningEffort"] = false,
                        ["supportsUsageInStreaming"] = false,
                    },
                    ["models"] = new object[]
                    {
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["id"] = modelId,
                            ["name"] = modelId,
                            ["reasoning"] = false,
                            ["input"] = new[] { "text" },
                            ["contextWindow"] = 128_000,
                            ["maxTokens"] = 16_384,
                            ["cost"] = new Dictionary<string, int>(StringComparer.Ordinal)
                            {
                                ["input"] = 0,
                                ["output"] = 0,
                                ["cacheRead"] = 0,
                                ["cacheWrite"] = 0,
                            },
                        },
                    },
                },
            },
        };
        return JsonSerializer.Serialize(document,
            new JsonSerializerOptions { WriteIndented = true });
    }

    internal static IReadOnlyList<string> ParseExtraArguments(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        if (raw.Length > 16_384 || raw.Contains('\0') || raw.Any(char.IsControl))
            throw new InvalidOperationException(
                "coder extra_args contain a control character or exceed the bounded limit.");

        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';
        var escaped = false;
        foreach (var c in raw)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                AddCurrent();
                continue;
            }
            current.Append(c);
        }
        if (escaped || quote != '\0')
            throw new InvalidOperationException(
                "coder extra_args contain an unterminated escape or quote.");
        AddCurrent();
        return result.AsReadOnly();

        void AddCurrent()
        {
            if (current.Length == 0) return;
            if (current.Length > MaxExtraArgumentChars || result.Count >= MaxExtraArguments)
                throw new InvalidOperationException(
                    "coder extra_args exceed the argument count or per-argument bound.");
            result.Add(current.ToString());
            current.Clear();
        }
    }

    private static void RejectExtraArguments(string raw, string tool, params string[] trustedNoOps)
    {
        var allowed = trustedNoOps.ToHashSet(StringComparer.Ordinal);
        if (ParseExtraArguments(raw).Any(argument => !allowed.Contains(argument)))
            throw new InvalidOperationException(
                $"{tool}.extra_args are unavailable in Docker mode because untrusted tool " +
                "aliases could override the trusted model, endpoint, workspace or session policy.");
    }
}
