using Tenninety.Cli;

namespace Tenninety.Tests;

public sealed class ProgramTests
{
    public static TheoryData<string[]> RejectedExtraArguments() => new()
    {
        new[] { "init", "extra" },
        new[] { "status", "--verbose" },
        new[] { "stop", "now" },
        new[] { "pause", "extra" },
        new[] { "resume", "extra" },
        new[] { "start", "--headless", "extra" },
        new[] { "plan", "--yes", "extra" },
        new[] { "plan", "--unknown" },
        new[] { "revert", "abc123", "extra" },
        new[] { "revert", "abc123", "--reason", "bad promotion", "extra" },
        new[] { "help", "extra" },
    };

    public static TheoryData<string[]> MissingOptionValues() => new()
    {
        new[] { "plan", "--spec" },
        new[] { "plan", "--spec", "--yes" },
        new[] { "plan", "--spec", "" },
        new[] { "revert", "abc123", "--reason" },
        new[] { "revert", "abc123", "--reason", "--unknown" },
        new[] { "revert", "abc123", "--reason", " " },
    };

    [Theory]
    [MemberData(nameof(RejectedExtraArguments))]
    public async Task Main_rejects_unknown_or_extra_arguments_before_dispatch(string[] args)
    {
        var (exitCode, error) = await RunMain(args);

        Assert.Equal(2, exitCode);
        Assert.Contains("argument", error);
    }

    [Theory]
    [MemberData(nameof(MissingOptionValues))]
    public async Task Main_rejects_options_with_missing_values(string[] args)
    {
        var (exitCode, error) = await RunMain(args);

        Assert.Equal(2, exitCode);
        Assert.Contains("requires a value", error);
    }

    [Fact]
    public void Revert_parser_keeps_the_reason_value_separate_from_the_commit()
    {
        var parsed = Program.ParseArguments(
            "revert", ["0123456789abcdef", "--reason", "bad promotion"]);

        Assert.Equal("0123456789abcdef", parsed.Commit);
        Assert.Equal("bad promotion", parsed.Reason);
    }

    [Fact]
    public async Task Main_rejects_an_unknown_command()
    {
        var (exitCode, error) = await RunMain(["frobnicate"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("unknown command 'frobnicate'", error);
    }

    [Fact]
    public async Task Unknown_command_diagnostic_is_sanitized_and_bounded_before_terminal_output()
    {
        const string secret = "supersecretvalue123";
        var command = "apiKey=" + secret + "\u001b[31m\r\0\u0085" + new string('x', 5000);

        var (exitCode, error) = await RunMain([command]);

        Assert.Equal(2, exitCode);
        Assert.True(error.Length < 2000);
        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', error);
        Assert.DoesNotContain('\r', error);
        Assert.DoesNotContain('\0', error);
        Assert.DoesNotContain('\u0085', error);
    }

    [Fact]
    public async Task Top_level_exception_diagnostic_is_sanitized_and_bounded_before_terminal_output()
    {
        const string secret = "supersecretvalue123";
        var hostileArgument = "apiKey=" + secret + "\u001b[31m\r\0\u0085" + new string('x', 5000);

        var (exitCode, error) = await RunMain(["status", hostileArgument]);

        Assert.Equal(2, exitCode);
        Assert.True(error.Length <= 4001);
        Assert.DoesNotContain(secret, error, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', error);
        Assert.DoesNotContain('\r', error);
        Assert.DoesNotContain('\0', error);
        Assert.DoesNotContain('\u0085', error);
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("-1")]
    [InlineData("-q")]
    public async Task Main_rejects_revert_targets_starting_with_a_dash(string target)
    {
        var (exitCode, _) = await RunMain(["revert", target]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Plan_parser_accepts_each_option_once_in_either_order()
    {
        var parsed = Program.ParseArguments("plan", ["--yes", "--spec", "docs/spec.md"]);

        Assert.True(parsed.Yes);
        Assert.Equal("docs/spec.md", parsed.SpecPath);
    }

    [Theory]
    [InlineData("plan", "--yes")]
    [InlineData("start", "--headless")]
    [InlineData("revert", "--reason")]
    public void Parser_rejects_duplicate_options(string command, string option)
    {
        var args = command == "revert"
            ? new[] { "abc123", option, "first", option, "second" }
            : new[] { option, option };

        var ex = Assert.Throws<ArgumentException>(() => Program.ParseArguments(command, args));
        Assert.Contains("only be specified once", ex.Message);
    }

    private static async Task<(int ExitCode, string Error)> RunMain(string[] args)
    {
        var originalError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var exitCode = await Program.Main(args);
            return (exitCode, error.ToString());
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
