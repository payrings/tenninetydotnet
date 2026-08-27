using Tenninety.Core.Models;

namespace Tenninety.Execution.OpenCode;

/// <summary>
/// Live coding agent backed by the OpenCode CLI (opencode.ai), run headless:
/// <c>opencode run --model provider/model --auto "&lt;instruction&gt;"</code>.
/// --auto approves permissions that are not explicitly denied, which is required for
/// unattended attempts; the engine owns the commit.
///
/// The model string is "provider/model" as configured in the user's OpenCode setup
/// (providers point at the local endpoint / llama-swap). Leave Model empty to use
/// whatever default the user's opencode config selects.
/// </summary>
public sealed class OpenCodeCoderAgent : CliCoderAgentBase
{
    private readonly string _modelOverride;
    private readonly string _extraArgs;

    public OpenCodeCoderAgent(string modelOverride, string extraArgs, TimeSpan attemptTimeout)
        : base(attemptTimeout)
    {
        _modelOverride = modelOverride;
        _extraArgs = extraArgs;
    }

    protected override string Executable => "opencode";

    public override IReadOnlyList<string> BuildArguments(string instruction)
    {
        var args = new List<string> { "run", "--auto" };
        if (!string.IsNullOrWhiteSpace(_modelOverride))
            args.AddRange(["--model", _modelOverride]);
        args.Add(instruction);
        args.AddRange(SplitExtraArgs(_extraArgs));
        return args;
    }
}
