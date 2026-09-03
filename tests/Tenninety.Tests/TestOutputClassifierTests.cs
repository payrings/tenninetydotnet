using Tenninety.Execution.Sandbox;
using Tenninety.Execution.Testing;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Pure classification of already-bounded command results: operational failures can never
/// pass, zero-test summaries fail closed, and the decision uses the COMPLETE output while the
/// report tail is only bounded presentation.
/// </summary>
public class TestOutputClassifierTests
{
    private static SandboxCommandResult Result(
        int exitCode = 0,
        string stdout = "",
        string stderr = "",
        bool timedOut = false,
        bool cancelled = false,
        bool oom = false,
        bool truncated = false) => new(
        ExitCode: exitCode,
        StdOutTail: stdout,
        StdErrTail: stderr,
        TimedOut: timedOut,
        Cancelled: cancelled,
        OomKilled: oom,
        OutputTruncated: truncated,
        Duration: TimeSpan.FromMilliseconds(5));

    [Fact]
    public void A_clean_successful_command_passes()
    {
        var c = TestOutputClassifier.Classify(Result(stdout: "Passed!  - Failed: 0, Passed: 12"));
        Assert.True(c.IsPass);
        Assert.False(c.ZeroTestsDetected);
        Assert.Empty(c.OperationalReason);
    }

    [Fact]
    public void A_nonzero_exit_does_not_pass()
    {
        var c = TestOutputClassifier.Classify(Result(exitCode: 1, stdout: "some failures"));
        Assert.False(c.IsPass);
        Assert.Contains("exited 1", c.OperationalReason);
    }

    [Theory]
    [InlineData(true, false, false, false, "timed out")]
    [InlineData(false, true, false, false, "cancelled")]
    [InlineData(false, false, true, false, "OOM")]
    [InlineData(false, false, false, true, "truncated")]
    public void Each_operational_failure_flag_prevents_a_pass_even_with_exit_zero(
        bool timedOut, bool cancelled, bool oom, bool truncated, string expectedReasonPart)
    {
        var c = TestOutputClassifier.Classify(Result(
            exitCode: 0, stdout: "everything looks great",
            timedOut: timedOut, cancelled: cancelled, oom: oom, truncated: truncated));

        Assert.False(c.IsPass);
        Assert.False(c.Succeeded);
        Assert.Contains(expectedReasonPart, c.OperationalReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Truncated_output_is_not_evidence_even_when_the_exit_code_is_zero()
    {
        var c = TestOutputClassifier.Classify(Result(exitCode: 0, stdout: "Passed! - Failed: 0, Passed: 5",
            truncated: true));
        Assert.False(c.IsPass);
    }

    [Theory]
    [InlineData("No test is available in the selected project", true)]
    [InlineData("No test matches the given testcase filter", true)]
    [InlineData("No tests were found in the assembly", true)]
    [InlineData("No test was executed", true)]
    [InlineData("No tests found", true)]
    [InlineData("No tests executed", true)]
    [InlineData("no tests found while scanning", true)]
    [InlineData("Total tests: 0", true)]
    [InlineData("Passed: 0", true)]
    [InlineData("Passed!  - Failed: 0, Passed: 7", false)]
    [InlineData("Total tests: 10", false)]
    public void Zero_test_summaries_are_detected_in_stdout(string stdout, bool zero)
    {
        var c = TestOutputClassifier.Classify(Result(stdout: stdout));
        Assert.Equal(zero, c.ZeroTestsDetected);
        Assert.NotEqual(zero, c.IsPass);
    }

    [Fact]
    public void Zero_test_summaries_are_detected_in_stderr_too()
    {
        var c = TestOutputClassifier.Classify(Result(stderr: "No tests were executed."));
        Assert.True(c.ZeroTestsDetected);
        Assert.False(c.IsPass);

        var shortForms = TestOutputClassifier.Classify(Result(stderr: "No tests found / No tests executed"));
        Assert.True(shortForms.ZeroTestsDetected);
        Assert.False(shortForms.IsPass);
    }

    [Fact]
    public void An_infrastructure_cancellation_is_classified_indeterminate_and_never_a_pass()
    {
        var c = TestOutputClassifier.Classify(Result(exitCode: 0, stdout: "partial output", cancelled: true));
        Assert.False(c.IsPass);
        Assert.False(c.Succeeded);
        Assert.Contains("indeterminate", c.OperationalReason);
    }

    [Fact]
    public void A_zero_test_message_at_the_very_start_is_never_lost_to_the_tail()
    {
        // The decision input is the complete output; the message sits far outside the final
        // 4000-character report tail.
        var stdout = "No test is available in the selected project\n" + new string('x', 9000);
        var c = TestOutputClassifier.Classify(Result(stdout: stdout));

        Assert.True(c.ZeroTestsDetected);
        Assert.False(c.IsPass);
        Assert.True(c.ReportTail.Length <= TestOutputClassifier.MaxReportTailChars);
        Assert.DoesNotContain("No test is available", c.ReportTail); // it is beyond the tail
    }

    [Fact]
    public void The_report_tail_is_bounded_and_sanitized()
    {
        // The secret sits inside the final 4000 characters so it is part of the tail.
        var stdout = new string('a', 5000) + "\napiKey: supersecretvalue123\n" + new string('b', 3000);
        var c = TestOutputClassifier.Classify(Result(stdout: stdout));

        Assert.True(c.ReportTail.Length <= TestOutputClassifier.MaxReportTailChars);
        Assert.DoesNotContain("supersecretvalue123", c.ReportTail);
        Assert.Contains("[REDACTED]", c.ReportTail);
    }

    [Fact]
    public void A_secret_assignment_crossing_the_tail_boundary_is_redacted_before_bounding()
    {
        // The presentation cut lands INSIDE the assignment identifier: truncating before
        // sanitizing would strip "SECRET_TOK" and leave the bare value exposed.
        var stdout = new string('a', 100) + "\nSECRET_TOKEN=supersecretvalue99\n" +
                     new string('b', 3974);
        var c = TestOutputClassifier.Classify(Result(stdout: stdout));

        Assert.True(c.ReportTail.Length <= TestOutputClassifier.MaxReportTailChars);
        Assert.DoesNotContain("supersecretvalue99", c.ReportTail);
        Assert.Contains("[REDACTED]", c.ReportTail);
    }

    [Fact]
    public void The_final_bound_is_applied_after_the_operational_reason_is_appended()
    {
        var full = TestOutputClassifier.Classify(Result(exitCode: 7, stdout: new string('x', 4000)));
        Assert.Equal(TestOutputClassifier.MaxReportTailChars, full.ReportTail.Length);

        var result = TestOutputClassifier.ToTestRunResult(full, "dotnet test", new string('a', 40));
        Assert.False(result.Passed);
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars,
            $"the final bound was not applied: {result.OutputTail.Length}");
        // The appended reason survives the final bound because it is bounded last.
        Assert.EndsWith("command exited 7.", result.OutputTail);
    }

    [Fact]
    public void ToTestRunResult_fails_closed_on_zero_tests_and_preserves_exit_codes()
    {
        var zero = TestOutputClassifier.Classify(Result(stdout: "Total tests: 0"));
        var zeroResult = TestOutputClassifier.ToTestRunResult(zero, "dotnet test", new string('a', 40));
        Assert.False(zeroResult.Passed);
        Assert.Equal(-1, zeroResult.ExitCode);
        Assert.Contains("zero tests were executed", zeroResult.OutputTail);
        Assert.Equal(new string('a', 40), zeroResult.CandidateSha);

        var failed = TestOutputClassifier.Classify(Result(exitCode: 3, stdout: "boom"));
        var failedResult = TestOutputClassifier.ToTestRunResult(failed, "dotnet test", new string('a', 40));
        Assert.False(failedResult.Passed);
        Assert.Equal(3, failedResult.ExitCode);

        var passed = TestOutputClassifier.Classify(Result(stdout: "Passed! 12"));
        var passedResult = TestOutputClassifier.ToTestRunResult(passed, "dotnet test", new string('a', 40));
        Assert.True(passedResult.Passed);
        Assert.Equal(0, passedResult.ExitCode);
    }
}
