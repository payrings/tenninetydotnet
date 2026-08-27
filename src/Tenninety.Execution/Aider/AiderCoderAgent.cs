using Tenninety.Core.Models;

namespace Tenninety.Execution.Aider;

/// <summary>
/// Live coding agent backed by the aider CLI. Aider edits the working tree directly;
/// the engine owns the commit (aider runs with --no-auto-commits), so attempt accounting
/// stays identical to every other coder backend.
/// </summary>
public sealed class AiderCoderAgent : CliCoderAgentBase
{
    private readonly TenNinetyConfig _config;
    private readonly string _localEndpoint;

    public AiderCoderAgent(TenNinetyConfig config, string localEndpoint)
        : base(TimeSpan.FromMinutes(Math.Max(1, config.AttemptTimeoutMinutes)))
    {
        _config = config;
        _localEndpoint = localEndpoint;
    }

    protected override string Executable => "aider";

    protected override string? ArtefactPrefix => ".aider*";

    /// <summary>Full aider model string. Empty derives openai/&lt;coder&gt;.</summary>
    public string ResolveModel() =>
        string.IsNullOrWhiteSpace(_config.Aider.Model)
            ? $"openai/{_config.LocalModels.Coder}"
            : _config.Aider.Model;

    public override IReadOnlyList<string> BuildArguments(string instruction)
    {
        // NOTE: the API key travels via the OPENAI_API_KEY environment variable
        // (set on the child process), never as a command-line argument – argv is
        // world-readable via /proc on Linux.
        var args = new List<string>
        {
            "--model", ResolveModel(),
            "--openai-api-base", _localEndpoint,
            "--message", instruction,
        };
        args.AddRange(SplitExtraArgs(_config.Aider.ExtraArgs));
        return args;
    }

    protected override void ConfigureEnvironment(System.Diagnostics.ProcessStartInfo psi)
    {
        psi.Environment["OPENAI_API_KEY"] =
            Environment.GetEnvironmentVariable("TENNINETY_LOCAL_API_KEY") ?? "dummy";
    }
}
