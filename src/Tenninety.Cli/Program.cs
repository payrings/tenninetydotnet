using Tenninety.Cli.Commands;

namespace Tenninety.Cli;

internal static class Program
{
    private const string Usage = """
        10/90 tenninety v3.2 — Spec-Driven Autonomous Framework

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
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var rest = args[1..];
        try
        {
            return command switch
            {
                "init" => InitCommand.Run(),
                "plan" => await PlanCommand.Run(TakeValue(rest, "--spec"), HasFlag(rest, "--yes")),
                "start" => await StartCommand.Run(HasFlag(rest, "--headless")),
                "status" => StatusCommand.Run(),
                "stop" => ControlCommands.Stop(),
                "pause" => ControlCommands.Pause(),
                "resume" => ControlCommands.Resume(),
                "revert" => await RevertCommand.Run(
                    rest.FirstOrDefault(a => !a.StartsWith("--"))
                        ?? throw new ArgumentException("usage: tenninety revert <commit> [--reason \"…\"]"),
                    TakeValue(rest, "--reason")),
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

    private static bool HasFlag(string[] args, string flag) => args.Contains(flag);

    private static string? TakeValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0 || index + 1 >= args.Length) return null;
        return args[index + 1];
    }
}
