using System.Collections.ObjectModel;
using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Execution.Coding;

/// <summary>
/// Frozen container-side invocation for one supported coding tool. The executable path and all
/// security-relevant flags are selected by trusted code. Docker mode rejects configured extra
/// arguments because aliases and future tool options cannot be safely denylisted.
/// </summary>
public sealed record CoderToolPlan(
    string Tool,
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment)
{
    private const int MaxInstructionChars = 131_072;
    private const int MaxExtraArguments = 128;
    private const int MaxExtraArgumentChars = 4096;

    public static CoderToolPlan Create(TenNinetyConfig config, CoderRunContext ctx)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Validate();

        var instruction = CliCoderAgentBase.BuildInstruction(ctx);
        if (instruction.Length > MaxInstructionChars)
            throw new InvalidOperationException(
                "the coder instruction exceeds the bounded container invocation limit.");

        var endpoint = config.Sandbox.Roles.Coder.ModelEndpoint.TrimEnd('/');
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
            "opencode" => BuildOpenCode(config, instruction, environment),
            "pi" => BuildPi(config, instruction, environment),
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
        TenNinetyConfig config, string instruction,
        IReadOnlyDictionary<string, string> environment)
    {
        if (string.IsNullOrWhiteSpace(config.OpenCode.Model))
            throw new InvalidOperationException(
                "opencode.model must be explicit for a containerized coder.");
        var args = new List<string>
        {
            "run", "--auto", "--model", config.OpenCode.Model, instruction,
        };
        RejectExtraArguments(config.OpenCode.ExtraArgs, "opencode");
        return new CoderToolPlan(
            "opencode", "/usr/local/bin/opencode", args.AsReadOnly(), environment);
    }

    private static CoderToolPlan BuildPi(
        TenNinetyConfig config, string instruction,
        IReadOnlyDictionary<string, string> environment)
    {
        if (string.IsNullOrWhiteSpace(config.Pi.Model))
            throw new InvalidOperationException(
                "pi.model must be explicit for a containerized coder.");
        var args = new List<string>
        {
            "-p", "--no-session", "--model", config.Pi.Model, instruction,
        };
        RejectExtraArguments(config.Pi.ExtraArgs, "pi");
        return new CoderToolPlan(
            "pi", "/usr/local/bin/pi", args.AsReadOnly(), environment);
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
