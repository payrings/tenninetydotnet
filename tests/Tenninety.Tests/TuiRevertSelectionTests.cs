using Tenninety.Git;
using Tenninety.Tui;

namespace Tenninety.Tests;

public sealed class TuiRevertSelectionTests
{
    private static readonly IReadOnlyList<GitCommit> Commits =
    [
        new("abcde11111111111111111111111111111111111", "newest", "test", "now"),
        new("abcde22222222222222222222222222222222222", "middle", "test", "now"),
        new("fedcb33333333333333333333333333333333333", "oldest", "test", "now"),
    ];

    [Theory]
    [InlineData("0", 0)]
    [InlineData("2", 2)]
    public void Valid_indexes_resolve_within_the_displayed_range(string selection, int expected)
    {
        var resolved = TuiHost.TryResolveRevertSelection(
            selection, Commits, out var target, out var error);

        Assert.True(resolved, error);
        Assert.Same(Commits[expected], target);
    }

    [Fact]
    public void Exact_sha_resolves_before_prefix_matching()
    {
        var selection = Commits[1].Sha.ToUpperInvariant();

        var resolved = TuiHost.TryResolveRevertSelection(
            selection, Commits, out var target, out var error);

        Assert.True(resolved, error);
        Assert.Same(Commits[1], target);
    }

    [Fact]
    public void All_decimal_exact_sha_is_not_misclassified_as_an_index()
    {
        var numeric = new GitCommit(new string('1', 40), "numeric", "test", "now");

        var resolved = TuiHost.TryResolveRevertSelection(
            numeric.Sha, [numeric], out var target, out var error);

        Assert.True(resolved, error);
        Assert.Same(numeric, target);
    }

    [Fact]
    public void Unique_sha_prefix_resolves_case_insensitively()
    {
        var resolved = TuiHost.TryResolveRevertSelection(
            "FEDCB", Commits, out var target, out var error);

        Assert.True(resolved, error);
        Assert.Same(Commits[2], target);
    }

    [Theory]
    [InlineData("3", "outside the displayed range")]
    [InlineData("١", "ASCII hexadecimal")]
    [InlineData("xyz", "ASCII hexadecimal")]
    [InlineData("dead", "not among recent commits")]
    [InlineData("", "cannot be blank")]
    [InlineData("   ", "cannot be blank")]
    [InlineData("abcde", "ambiguous")]
    public async Task Invalid_or_ambiguous_selection_never_invokes_revert(
        string selection, string expectedError)
    {
        var calls = 0;

        var result = await TuiHost.ResolveAndRevertAsync(
            selection,
            Commits,
            (commit, ct) =>
            {
                calls++;
                return Task.FromResult(commit.Sha);
            },
            CancellationToken.None);

        Assert.Contains(expectedError, result, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Oversized_numeric_selection_never_throws_or_invokes_revert()
    {
        var calls = 0;

        var result = await TuiHost.ResolveAndRevertAsync(
            new string('9', 2000),
            Commits,
            (commit, ct) =>
            {
                calls++;
                return Task.FromResult(commit.Sha);
            },
            CancellationToken.None);

        Assert.Contains("outside the displayed range", result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Valid_selection_invokes_revert_once_with_the_full_sha()
    {
        string? invokedSha = null;

        var result = await TuiHost.ResolveAndRevertAsync(
            "fedcb",
            Commits,
            (commit, ct) =>
            {
                invokedSha = commit.Sha;
                return Task.FromResult("reverted");
            },
            CancellationToken.None);

        Assert.Equal("reverted", result);
        Assert.Equal(Commits[2].Sha, invokedSha);
    }
}
