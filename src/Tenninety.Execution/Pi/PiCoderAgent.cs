using Tenninety.Core.Models;

namespace Tenninety.Execution.Pi;

/// <summary>
/// Live coding agent backed by the Pi coding-agent CLI (pi.dev), run in print mode:
/// <c>pi -p --no-session [--model provider/model] "&lt;instruction&gt;"</code>.
/// -p makes the run non-interactive (prints response and exits); --no-session keeps
/// attempts ephemeral; the engine owns the commit.
///
/// The model string follows pi's "provider/id" pattern (custom local providers are set up
/// via ~/.pi/agent/models.json; llama.cpp router is built in). Leave Model empty to use
/// whatever model the user's pi settings select.
/// </summary>
public sealed class PiCoderAgent : CliCoderAgentBase
{
    private readonly string _modelOverride;
    private readonly string _extraArgs;

    public PiCoderAgent(string modelOverride, string extraArgs, TimeSpan attemptTimeout)
        : base(attemptTimeout)
    {
        _modelOverride = modelOverride;
        _extraArgs = extraArgs;
    }

    protected override string Executable => "pi";

    public override IReadOnlyList<string> BuildArguments(string instruction)
    {
        // Credentials are NOT passed on the command line: configure the local provider
        // (with its key) in ~/.pi/agent/models.json per pi's documentation.
        var args = new List<string> { "-p", "--no-session" };
        if (!string.IsNullOrWhiteSpace(_modelOverride))
            args.AddRange(["--model", _modelOverride]);
        args.Add(instruction);
        args.AddRange(SplitExtraArgs(_extraArgs));
        return args;
    }
}
