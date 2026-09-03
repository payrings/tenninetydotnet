using System.Reflection;
using System.Text.Json;
using Tenninety.Core.Models;
using Tenninety.Execution.Candidates;
using Tenninety.Execution.Testing;
using Xunit;

namespace Tenninety.Tests;

/// <summary>
/// Phase 5A unit A: the Tester-only context carries trusted candidate identity and nothing
/// host-related, and result identity can never be silently absent from a passing gate's input.
/// </summary>
public class TesterContextTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef01234567";

    private static TesterRunContext MakeContext(
        string? sha = ValidSha, string? wpId = "WP-001", int attempt = 1) => new()
    {
        Candidate = new CandidateRevision("work/WP-001", sha!, "main-base-sha-value"),
        WorkPackageId = wpId!,
        Attempt = attempt,
    };

    // ---- identity validation -----------------------------------------------------------

    [Fact]
    public void A_valid_context_passes_validation()
    {
        MakeContext().Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]   // 39 chars
    [InlineData("0123456789abcdef0123456789abcdef012345678")]   // 41 chars
    [InlineData("0123456789ABCDEF0123456789abcdef01234567")]   // uppercase is not canonical
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]   // non-hex
    [InlineData("0123456789abcdef0123456789abcdef0123456 ")]   // whitespace
    public void Malformed_candidate_shas_fail_validation(string? sha)
    {
        Assert.Throws<InvalidOperationException>(() => MakeContext(sha: sha).Validate());
    }

    [Theory]
    [InlineData("WP-001")]
    [InlineData("HOTFIX")]
    [InlineData("wp_2-a")]
    [InlineData("A")]
    public void Bounded_ascii_identifiers_are_accepted(string wpId)
    {
        MakeContext(wpId: wpId).Validate();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("WP 001")]
    [InlineData("WP-001;rm")]
    [InlineData("WP-001\n")]
    [InlineData("ünïcödé")]
    public void Hostile_work_package_identifiers_fail_validation(string? wpId)
    {
        Assert.Throws<InvalidOperationException>(() => MakeContext(wpId: wpId).Validate());
    }

    [Fact]
    public void A_work_package_identifier_over_64_characters_fails_validation()
    {
        Assert.Throws<InvalidOperationException>(
            () => MakeContext(wpId: new string('a', 65)).Validate());
    }

    [Fact]
    public void Non_positive_attempts_fail_validation()
    {
        Assert.Throws<InvalidOperationException>(() => MakeContext(attempt: 0).Validate());
    }

    [Fact]
    public void A_null_candidate_fails_validation()
    {
        var ctx = new TesterRunContext
        {
            Candidate = null!,
            WorkPackageId = "WP-001",
            Attempt = 1,
        };
        Assert.Throws<InvalidOperationException>(ctx.Validate);
    }

    // ---- the context cannot carry host-oriented data ------------------------------------

    [Fact]
    public void The_context_has_no_host_path_mount_docker_or_launcher_members()
    {
        var forbidden = new[]
        {
            "Repo", "Path", "Host", "Mount", "Docker", "Ingestion", "Workspace",
            "Launcher", "Process", "Command", "Directory",
        };
        var propertyNames = typeof(TesterRunContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        Assert.Equal(
            new[] { "Candidate", "WorkPackageId", "Attempt", "Advice" }.OrderBy(n => n),
            propertyNames.OrderBy(n => n));
        foreach (var property in propertyNames)
            Assert.False(
                forbidden.Any(f => property.Contains(f, StringComparison.OrdinalIgnoreCase)),
                $"the tester context must not carry a host-oriented member like '{property}'.");
    }

    [Fact]
    public void The_candidate_revision_is_the_only_identity_carrier()
    {
        var propertyTypes = typeof(TesterRunContext)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => p.Name, p => p.PropertyType);
        Assert.Equal(typeof(CandidateRevision), propertyTypes["Candidate"]);
    }

    // ---- defensive snapshotting ----------------------------------------------------------

    [Fact]
    public void Advice_is_snapshot_so_later_mutation_cannot_change_the_context()
    {
        var advice = new List<string> { "check the retry budget" };
        var ctx = MakeContext();
        var withAdvice = new TesterRunContext
        {
            Candidate = ctx.Candidate,
            WorkPackageId = ctx.WorkPackageId,
            Attempt = 1,
            Advice = advice,
        };
        advice.Add("injected after construction");
        advice[0] = "rewritten after construction";

        Assert.Equal(new[] { "check the retry budget" }, withAdvice.Advice);
    }

    // ---- result identity behavior --------------------------------------------------------

    [Fact]
    public void TestRunResult_candidate_sha_defaults_to_null_and_is_never_self_proven()
    {
        var result = new TestRunResult { Passed = true, ExitCode = 0 };
        Assert.Null(result.CandidateSha);
        // Null identity + success is exactly the combination the engine must reject.
        Assert.False(IsAcceptablePass(result, ValidSha));
    }

    [Fact]
    public void TestRunResult_serialization_preserves_the_candidate_sha_convention()
    {
        var result = new TestRunResult
        {
            Passed = false,
            ExitCode = 1,
            OutputTail = "tail",
            Command = "dotnet test",
            CandidateSha = ValidSha,
        };
        var roundTrip = JsonSerializer.Deserialize<TestRunResult>(
            JsonSerializer.Serialize(result));

        Assert.NotNull(roundTrip);
        Assert.Equal(ValidSha, roundTrip!.CandidateSha);
        Assert.Equal(result.Passed, roundTrip.Passed);
        Assert.Equal(result.ExitCode, roundTrip.ExitCode);
        Assert.Equal(result.OutputTail, roundTrip.OutputTail);
        Assert.Equal(result.Command, roundTrip.Command);
    }

    [Fact]
    public void Result_identity_compares_exact_candidate_strings()
    {
        // Ordinal comparison: no normalization, no casing escape hatch.
        Assert.True(string.Equals(ValidSha, ValidSha, StringComparison.Ordinal));
        Assert.False(string.Equals(ValidSha, ValidSha.ToUpperInvariant(), StringComparison.Ordinal));
        Assert.False(string.Equals(ValidSha, null, StringComparison.Ordinal));
        Assert.False(string.Equals(ValidSha, "", StringComparison.Ordinal));
    }

    /// <summary>Mirrors the engine/revert gate rule for test-level proof: only a pass that
    /// binds exactly to the requested candidate is acceptable.</summary>
    private static bool IsAcceptablePass(TestRunResult result, string requestedSha) =>
        result.Passed && string.Equals(result.CandidateSha, requestedSha, StringComparison.Ordinal);
}
