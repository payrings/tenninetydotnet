using System.Text.RegularExpressions;
using Tenninety.Core.Models;
using Tenninety.Execution.Sandbox;

namespace Tenninety.Execution.Testing;

/// <summary>One bounded, pure interpretation of an already-bounded command result. The
/// decision uses the COMPLETE captured output; the report tail is only presentation.</summary>
public sealed record TestOutputClassification(
    int ExitCode,
    bool Succeeded,
    bool ZeroTestsDetected,
    string OperationalReason,
    string ReportTail)
{
    public bool IsPass => Succeeded && !ZeroTestsDetected && OperationalReason.Length == 0;
}

/// <summary>
/// Pure interpretation of command results for the mechanical test gate. It never executes
/// anything and never touches the filesystem. Rules:
///
///  - success requires <see cref="SandboxCommandResult.Succeeded"/> (zero exit AND no timeout,
///    cancellation, OOM kill or output truncation) — never a bare exit-code check;
///  - timeout, cancellation, OOM, truncation and synthetic operational exits can never pass;
///    an infrastructure-layer `Cancelled=true` result without caller cancellation is an
///    indeterminate infrastructure failure for the caller to classify (the gate throws);
///  - explicit zero-test detection covers "no test is available", "no test matches", "no
///    tests found/executed" (with or without an auxiliary verb), "Passed: 0", "Total tests: 0"
///    and the other existing zero-test summaries, matched against the COMPLETE bounded
///    captured output so a zero-test message near the beginning cannot disappear from the
///    decision because later output was appended;
///  - the COMPLETE already-bounded output is SANITIZED BEFORE the presentation tail is
///    selected: truncating first could cut a secret's identifying prefix while keeping its
///    value; the final bound is applied after operational reasons and zero-test/build-failure
///    suffixes are appended (see <see cref="FinalBound"/>).
///
/// Test-command output is decision input only; it is never tamper-proof evidence that real
/// test coverage exists.
/// </summary>
public static partial class TestOutputClassifier
{
    /// <summary>Explicit bounded output cap submitted with every tester command (1 MiB).</summary>
    public const long MaxCommandOutputBytes = 1_048_576;

    /// <summary>Bounded user-facing report tail length (characters).</summary>
    public const int MaxReportTailChars = 4000;

    [GeneratedRegex(
        @"No test is available|No test matches|No tests? (?:were|was) (?:found|executed)|No tests? (?:found|executed)|Passed:\s*0\b|Total(?: tests)?:\s*0\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ZeroTestsOutput();

    public static TestOutputClassification Classify(SandboxCommandResult result)
    {
        // The complete bounded captured output decides; the tail is reduced afterwards.
        var combined = (result.StdOutTail + "\n" + result.StdErrTail).Trim();
        var zeroTests = result.Succeeded && ZeroTestsOutput().IsMatch(combined);
        // Sanitize the complete output BEFORE selecting the presentation tail so bounding can
        // never strip a secret's identifying prefix while retaining its value.
        var tail = FinalBound(Core.Security.Sanitizer.SanitizeText(combined));

        var reason = (result.TimedOut, result.Cancelled, result.OomKilled, result.OutputTruncated) switch
        {
            (true, _, _, _) => $"command timed out after {result.Duration.TotalSeconds:0.#}s.",
            (_, true, _, _) => "command was cancelled; the result is indeterminate.",
            (_, _, true, _) => "command was OOM-killed.",
            (_, _, _, true) => "command output was truncated; the result is not evidence of success.",
            _ => result.ExitCode == 0 ? "" : $"command exited {result.ExitCode}.",
        };

        return new TestOutputClassification(
            ExitCode: result.ExitCode,
            Succeeded: result.Succeeded,
            ZeroTestsDetected: zeroTests,
            OperationalReason: reason,
            ReportTail: tail);
    }

    /// <summary>Builds a failed <see cref="TestRunResult"/> from a classification with the
    /// zero-test escape hatch applied (a successful command that ran zero tests fails closed).
    /// The zero-test detection can be disabled for the BUILD stage, whose success never
    /// depends on zero-test-looking output. The FINAL presentation bound is applied after the
    /// operational reason and the zero-test explanation are appended.</summary>
    public static TestRunResult ToTestRunResult(
        TestOutputClassification classification, string commandLabel, string candidateSha,
        bool zeroTestsFailClosed = true)
    {
        var tail = classification.ReportTail;
        if (classification.OperationalReason.Length > 0)
            tail = string.IsNullOrEmpty(tail)
                ? classification.OperationalReason
                : tail + "\n" + classification.OperationalReason;
        var zero = zeroTestsFailClosed && classification.ZeroTestsDetected;
        if (zero)
            tail += "\nzero tests were executed - failing closed.";
        return new TestRunResult
        {
            Passed = classification.Succeeded &&
                     classification.OperationalReason.Length == 0 &&
                     !(zeroTestsFailClosed && classification.ZeroTestsDetected),
            ExitCode = zero ? -1 : classification.ExitCode,
            Command = commandLabel,
            OutputTail = FinalBound(tail),
            CandidateSha = candidateSha,
        };
    }

    /// <summary>The final presentation bound: applied LAST, after every operational reason,
    /// zero-test explanation and build-failure suffix has been appended.</summary>
    public static string FinalBound(string value) =>
        value.Length <= MaxReportTailChars ? value : value[^MaxReportTailChars..];
}
