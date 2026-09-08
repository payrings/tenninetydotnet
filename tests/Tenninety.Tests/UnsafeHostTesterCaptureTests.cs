using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Testing;
using Tenninety.Git;

namespace Tenninety.Tests;

/// <summary>
/// The unsafe-host Tester classifies the COMPLETE bounded captured output — never a
/// pre-shortened 4,000-character presentation tail. A zero-test summary that occurs EARLY
/// followed by more than 4,000 characters of later output still fails the gate closed, and
/// output beyond the decision-input cap fails closed via the typed truncation flag.
/// </summary>
public sealed class UnsafeHostTesterCaptureTests : IDisposable
{
    private readonly TempDir _repo = new();

    public void Dispose() => _repo.Dispose();

    private UnsafeHostTesterAgent Agent(Action<string>? log = null) => new(
        new GitService(_repo.Root),
        commandTemplate: "cat fixture.txt",
        buildCommand: "",
        attemptTimeout: TimeSpan.FromMinutes(2),
        log,
        failWhenNoProject: true);

    private string CandidateSha()
    {
        var git = new GitService(_repo.Root);
        git.Init();
        File.WriteAllText(_repo.Path("README.md"), "candidate\n");
        return git.CommitAll("candidate")!;
    }

    private static string OutputWithEarlyZeroTest(int trailingChars)
        => "No test is available in this workspace.\n" + new string('x', trailingChars);

    [Fact]
    public async Task A_zero_test_message_early_in_huge_output_still_fails_closed()
    {
        // The fixture emits ~12 KB: the zero-test line sits at the very beginning, so any
        // tail-only classifier (last 4,000 chars) would MISS it and wrongly pass.
        File.WriteAllText(_repo.Path("fixture.txt"), OutputWithEarlyZeroTest(12_000));
        File.WriteAllText(_repo.Path("README.md"), "demo\n");
        // The workspace contains a recognizable "test project" so discovery passes and the
        // configured command actually runs.
        File.WriteAllText(_repo.Path("sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        var sha = CandidateSha();

        var result = await Agent().RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("zero tests were executed - failing closed", result.OutputTail);
        // The presentation tail is still bounded for display.
        Assert.True(result.OutputTail.Length <= TestOutputClassifier.MaxReportTailChars + 64);
    }

    [Fact]
    public async Task Output_beyond_the_decision_input_cap_fails_closed()
    {
        // 2 MiB of output: 1 MiB over the classification cap. Truncated decision input can
        // never pass, regardless of the exit code.
        File.WriteAllText(_repo.Path("fixture.txt"), new string('o', 2 * 1024 * 1024));
        WriteTestProjectFixture();
        var sha = CandidateSha();

        var result = await Agent().RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Contains("not evidence of success", result.OutputTail);
    }

    [Fact]
    public async Task Stdout_only_overflow_fails_closed_under_the_shared_budget()
    {
        // stdout alone exceeds the aggregate cap (stderr silent).
        WriteTestProjectFixture();
        var sha = CandidateSha();
        var agent = new UnsafeHostTesterAgent(
            new GitService(_repo.Root),
            commandTemplate: "head -c 1200000 /dev/zero | tr '\\0' 'o'", // 1.2 MiB on stdout
            buildCommand: "",
            attemptTimeout: TimeSpan.FromMinutes(2),
            failWhenNoProject: true);

        var result = await agent.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Contains("not evidence of success", result.OutputTail);
    }

    [Fact]
    public async Task Stderr_only_overflow_fails_closed_under_the_shared_budget()
    {
        // stderr alone exceeds the aggregate cap (stdout silent).
        WriteTestProjectFixture();
        var sha = CandidateSha();
        var agent = new UnsafeHostTesterAgent(
            new GitService(_repo.Root),
            commandTemplate: "head -c 1200000 /dev/zero | tr '\\0' 'e' >&2", // 1.2 MiB on stderr
            buildCommand: "",
            attemptTimeout: TimeSpan.FromMinutes(2),
            failWhenNoProject: true);

        var result = await agent.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Contains("not evidence of success", result.OutputTail);
    }

    [Fact]
    public async Task Combined_stream_overflow_fails_closed_even_when_neither_stream_reaches_the_limit()
    {
        // 700 KiB on stdout and 700 KiB on stderr: each stream stays under the 1 MiB
        // aggregate cap alone, but the SHARED budget (1 MiB total) is exhausted — the old
        // per-stream caps would have accepted this as untruncated.
        WriteTestProjectFixture();
        var sha = CandidateSha();
        var agent = new UnsafeHostTesterAgent(
            new GitService(_repo.Root),
            commandTemplate:
                "head -c 716800 /dev/zero | tr '\\0' 'o'; " +
                "head -c 716800 /dev/zero | tr '\\0' 'e' >&2",
            buildCommand: "",
            attemptTimeout: TimeSpan.FromMinutes(2),
            failWhenNoProject: true);

        var result = await agent.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Contains("not evidence of success", result.OutputTail);
    }

    [Fact]
    public void The_shared_budget_never_grants_more_than_its_limit()
    {
        // Direct concurrency proof for the aggregate budget: many racing reservations must
        // grant exactly the limit in total, and every request beyond it is cut.
        var budget = new UnsafeHostTesterAgent.OutputBudget(1_000);
        long total = 0;
        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < 200; i++)
                Interlocked.Add(ref total, budget.Reserve(64));
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(1_000, total);
        Assert.Equal(1_000, budget.Granted);
        Assert.Equal(0, budget.Reserve(1)); // exhausted: nothing further is granted
    }

    private void WriteTestProjectFixture()
    {
        // The workspace contains a recognizable "test project" so discovery passes and the
        // configured command actually runs.
        File.WriteAllText(_repo.Path("sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
    }

    [Fact]
    public async Task A_passing_command_without_zero_test_phrases_is_reported_passed()
    {
        File.WriteAllText(_repo.Path("fixture.txt"), "all 3 tests passed\nPassed: 3\n");
        File.WriteAllText(_repo.Path("README.md"), "demo\n");
        File.WriteAllText(_repo.Path("sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        var sha = CandidateSha();

        var result = await Agent().RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.True(result.Passed);
        Assert.Equal(sha, result.CandidateSha);
        // The presentation tail keeps the classified (sanitized) output, not raw bytes.
        Assert.Contains("all 3 tests passed", result.OutputTail);
    }

    [Fact]
    public async Task A_zero_test_phrase_in_a_failed_command_is_classified_as_an_ordinary_failure()
    {
        File.WriteAllText(_repo.Path("README.md"), "demo\n");
        File.WriteAllText(_repo.Path("sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        var sha = CandidateSha();

        var agent = new UnsafeHostTesterAgent(
            new GitService(_repo.Root),
            commandTemplate: "echo 'No tests executed'; exit 3",
            buildCommand: "",
            attemptTimeout: TimeSpan.FromMinutes(1),
            failWhenNoProject: true);

        var result = await agent.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("command exited 3", result.OutputTail);
    }

    [Fact]
    public async Task A_failing_build_stops_before_tests_and_never_fails_on_zero_test_phrases()
    {
        File.WriteAllText(_repo.Path("README.md"), "demo\n");
        File.WriteAllText(_repo.Path("sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        var sha = CandidateSha();

        var agent = new UnsafeHostTesterAgent(
            new GitService(_repo.Root),
            commandTemplate: "echo 'should never run'",
            buildCommand: "echo 'No tests were found during build'; exit 1",
            attemptTimeout: TimeSpan.FromMinutes(1),
            failWhenNoProject: true);

        var result = await agent.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Contains("the build failed; tests were never started", result.OutputTail);
        Assert.DoesNotContain("should never run", result.OutputTail);
    }

    [Fact]
    public async Task A_timed_out_command_can_never_pass()
    {
        File.WriteAllText(_repo.Path("README.md"), "demo\n");
        File.WriteAllText(_repo.Path("sample.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
            "<PackageReference Include=\"xunit\" Version=\"2.9.3\" /></ItemGroup></Project>");
        var sha = CandidateSha();

        var agent = new UnsafeHostTesterAgent(
            new GitService(_repo.Root),
            commandTemplate: "sleep 60",
            buildCommand: "",
            attemptTimeout: TimeSpan.FromSeconds(2),
            failWhenNoProject: true);

        var result = await agent.RunTestsAsync(new TesterRunContext
        {
            Candidate = new CandidateRevision("main", sha, sha),
            WorkPackageId = "WP-001",
            Attempt = 1,
        });

        Assert.False(result.Passed);
        Assert.Contains("timed out", result.OutputTail);
    }
}
