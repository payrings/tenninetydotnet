using Tenninety.Cli.Commands;

namespace Tenninety.Cli;

internal static class Program
{
    private const string Usage = """
        10/90 tenninety — Spec-Driven Autonomous Framework

        Usage:
          tenninety init                          Scaffold .tenninety/, config, git repo, starter spec
          tenninety plan [--spec <path>] [--yes]  Frontier decomposes spec.md into plan.json
          tenninety start [--headless]            Run the autonomous serial queue (TUI by default)
          tenninety status                        Print queue & system health snapshot
          tenninety pause | resume | stop         Cooperative daemon controls
          tenninety revert <commit> [--reason …]  Hotfix-revert a bad promotion

        Options:
          -h, --help    Show this help
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];
        try
        {
            if (command is "-h" or "--help" or "help")
            {
                RequireNoArguments(command, rest);
                Console.WriteLine(Usage);
                return 0;
            }

            var parsed = ParseArguments(command, rest);
            return command switch
            {
                "init" => InitCommand.Run(),
                "plan" => await PlanCommand.Run(parsed.SpecPath, parsed.Yes),
                "start" => await StartCommand.Run(parsed.Headless),
                "status" => StatusCommand.Run(),
                "stop" => ControlCommands.Stop(),
                "pause" => ControlCommands.Pause(),
                "resume" => ControlCommands.Resume(),
                "revert" => await RevertCommand.Run(
                    parsed.Commit ?? throw new InvalidOperationException(
                        "validated revert arguments did not contain a commit."),
                    parsed.Reason),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex is ArgumentException ? 2 : 1;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'.\n\n{Usage}");
        return 2;
    }

    internal sealed record ParsedArguments(
        string? SpecPath = null,
        bool Yes = false,
        bool Headless = false,
        string? Commit = null,
        string? Reason = null);

    internal static ParsedArguments ParseArguments(string command, string[] args) => command switch
    {
        "init" or "status" or "stop" or "pause" or "resume" =>
            RequireNoArguments(command, args),
        "plan" => ParsePlanArguments(args),
        "start" => ParseStartArguments(args),
        "revert" => ParseRevertArguments(args),
        _ => new ParsedArguments(),
    };

    private static ParsedArguments RequireNoArguments(string command, string[] args)
    {
        if (args.Length > 0)
            throw UnexpectedArgument(command, args[0]);
        return new ParsedArguments();
    }

    private static ParsedArguments ParsePlanArguments(string[] args)
    {
        string? specPath = null;
        var yes = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--spec":
                    if (specPath is not null)
                        throw new ArgumentException("option '--spec' may only be specified once.");
                    specPath = TakeRequiredValue(args, ref i, "--spec");
                    break;
                case "--yes":
                    if (yes)
                        throw new ArgumentException("option '--yes' may only be specified once.");
                    yes = true;
                    break;
                default:
                    throw UnexpectedArgument("plan", args[i]);
            }
        }
        return new ParsedArguments(SpecPath: specPath, Yes: yes);
    }

    private static ParsedArguments ParseStartArguments(string[] args)
    {
        if (args.Length == 0)
            return new ParsedArguments();
        if (args.Length == 1 && args[0] == "--headless")
            return new ParsedArguments(Headless: true);
        if (args.Length > 1 && args[0] == "--headless" && args[1] == "--headless")
            throw new ArgumentException("option '--headless' may only be specified once.");
        throw UnexpectedArgument("start", args.Length > 1 && args[0] == "--headless"
            ? args[1]
            : args[0]);
    }

    private static ParsedArguments ParseRevertArguments(string[] args)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]) ||
            args[0].StartsWith('-'))
            throw new ArgumentException("usage: tenninety revert <commit> [--reason <text>]");

        var commit = args[0];
        string? reason = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] != "--reason")
                throw UnexpectedArgument("revert", args[i]);
            if (reason is not null)
                throw new ArgumentException("option '--reason' may only be specified once.");
            reason = TakeRequiredValue(args, ref i, "--reason");
        }
        return new ParsedArguments(Commit: commit, Reason: reason);
    }

    private static string TakeRequiredValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
            args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"option '{option}' requires a value.");
        index++;
        return args[index];
    }

    private static ArgumentException UnexpectedArgument(string command, string argument) =>
        new($"unexpected argument '{argument}' for command '{command}'.");
}
