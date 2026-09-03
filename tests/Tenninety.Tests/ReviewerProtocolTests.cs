using System.Text;
using Tenninety.Execution.Reviewing;

namespace Tenninety.Tests;

public sealed class ReviewerProtocolTests
{
    [Fact]
    public void Exact_command_and_response_bounds_are_accepted()
    {
        var json = "{\"action\":\"run\",\"command\":\"" +
                   new string('x', ReviewerProtocol.MaxCommandChars) + "\"}";

        var parsed = Assert.IsType<ReviewerCommandResponse>(
            ReviewerProtocol.Parse(json, Encoding.UTF8.GetByteCount(json)));

        Assert.Equal(ReviewerProtocol.MaxCommandChars, parsed.Command.Length);
    }

    [Fact]
    public void One_byte_over_the_response_bound_is_rejected()
    {
        const string json = "{\"action\":\"final\",\"verdict\":\"PASS\",\"reasons\":[]}";
        Assert.Throws<ReviewerProtocolException>(() =>
            ReviewerProtocol.Parse(json, Encoding.UTF8.GetByteCount(json) - 1));
    }

    [Fact]
    public void Command_and_reason_shape_limits_fail_closed()
    {
        var longCommand = "{\"action\":\"run\",\"command\":\"" +
                          new string('x', ReviewerProtocol.MaxCommandChars + 1) + "\"}";
        var longReason = "{\"action\":\"final\",\"verdict\":\"FAIL\",\"reasons\":[\"" +
                         new string('x', ReviewerProtocol.MaxReasonChars + 1) + "\"]}";
        var tooManyReasons = "{\"action\":\"final\",\"verdict\":\"FAIL\",\"reasons\":[" +
                             string.Join(',', Enumerable.Repeat("\"reason\"", ReviewerProtocol.MaxReasons + 1)) + "]}";

        Assert.Throws<ReviewerProtocolException>(() => ReviewerProtocol.Parse(longCommand, 100_000));
        Assert.Throws<ReviewerProtocolException>(() => ReviewerProtocol.Parse(longReason, 100_000));
        Assert.Throws<ReviewerProtocolException>(() => ReviewerProtocol.Parse(tooManyReasons, 100_000));
    }

    [Theory]
    [InlineData("```json\n{\"action\":\"final\",\"verdict\":\"PASS\",\"reasons\":[]}\n```")]
    [InlineData("{\"action\":\"run\",\"action\":\"final\",\"command\":\"pwd\"}")]
    [InlineData("{\"action\":\"run\",\"command\":\"pwd\",\"unknown\":1}")]
    [InlineData("{\"Action\":\"run\",\"command\":\"pwd\"}")]
    [InlineData("{\"action\":\"run\",\"command\":\"pwd\"}{}")]
    [InlineData("{\"action\":\"run\",\"command\":\"line\\nnext\"}")]
    [InlineData("{\"action\":\"final\",\"verdict\":\"PASS\",\"reasons\":[\"no\"]}")]
    [InlineData("{\"action\":\"final\",\"verdict\":\"FAIL\",\"reasons\":[]}")]
    [InlineData("{\"action\":\"unknown\"}")]
    [InlineData("[]")]
    [InlineData("null")]
    public void Hostile_or_contradictory_protocol_data_is_rejected(string json) =>
        Assert.Throws<ReviewerProtocolException>(() => ReviewerProtocol.Parse(json, 100_000));

    [Fact]
    public void Valid_pass_and_fail_verdicts_are_exact()
    {
        var pass = Assert.IsType<ReviewerVerdictResponse>(ReviewerProtocol.Parse(
            "{\"action\":\"final\",\"verdict\":\"PASS\",\"reasons\":[]}", 1024));
        var fail = Assert.IsType<ReviewerVerdictResponse>(ReviewerProtocol.Parse(
            "{\"action\":\"final\",\"verdict\":\"FAIL\",\"reasons\":[\"missing test\"]}", 1024));

        Assert.True(pass.Passed);
        Assert.Empty(pass.Reasons);
        Assert.False(fail.Passed);
        Assert.Equal(["missing test"], fail.Reasons);
    }
}
