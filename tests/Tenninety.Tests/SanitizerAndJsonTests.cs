using Tenninety.Core.Security;
using Tenninety.Frontier;

namespace Tenninety.Tests;

public class SanitizerTests
{
    [Theory]
    [InlineData("key = sk-abcdefghijklmnop123456", "sk-abcdefghijklmnop123456")]
    [InlineData("token: ghp_" + "abcdefghijklmnopqrstuvwxyz012345", "ghp_abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("AWS_KEY=AKIAIOSFODNN7EXAMPLEX", "AKIAIOSFODNN7EXAMPLEX")]
    public void Known_token_formats_are_redacted(string input, string secret)
    {
        var output = Sanitizer.SanitizeText(input);
        Assert.DoesNotContain(secret, output);
        Assert.Contains("REDACTED", output);
    }

    [Fact]
    public void Env_assignments_are_redacted()
    {
        var output = Sanitizer.SanitizeText("DB_PASSWORD=super-secret-value\nPORT=5432");
        Assert.DoesNotContain("super-secret-value", output);
        Assert.Contains("PORT=5432", output); // non-secret assignments survive
    }

    [Theory]
    [InlineData("Authorization: Basic dXNlcjpzdXBlcnNlY3JldA==", "dXNlcjpzdXBlcnNlY3JldA==")]
    [InlineData("{\"client_secret\":\"supersecretvalue123\"}", "supersecretvalue123")]
    [InlineData("{\"access_token\":\"abc/def+ghi=12345\"}", "abc/def+ghi=12345")]
    [InlineData("{\"password\":\"correct-horse-battery\"}", "correct-horse-battery")]
    public void Common_credential_shapes_are_redacted(string input, string secret)
    {
        var output = Sanitizer.SanitizeText(input);
        Assert.DoesNotContain(secret, output);
        Assert.Contains("REDACTED", output);
    }

    [Fact]
    public void Private_key_blocks_are_removed()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIB\nabc\n-----END RSA PRIVATE KEY-----";
        var output = Sanitizer.SanitizeText($"data before {pem} data after");
        Assert.DoesNotContain("MIIB", output);
        Assert.Contains("data before", output);
        Assert.Contains("data after", output);
    }

    [Theory]
    [InlineData(".env", true)]
    [InlineData(".env.local", true)]
    [InlineData("server.pem", true)]
    [InlineData("id_rsa", true)]       // matches "id_rsa*" (bare key or id_rsa.pub etc.)
    [InlineData("id_rsa.pub", true)]
    [InlineData("Program.cs", false)]
    public void Excluded_file_detection(string path, bool excluded) =>
        Assert.Equal(excluded, Sanitizer.IsExcludedFile(path));

    [Fact]
    public void Excluded_file_diagnostic()
    {
        Assert.False(Sanitizer.IsExcludedFile("src/Services/Program.cs"));
    }
}

public class JsonExtractorTests
{
    [Fact]
    public void Extracts_from_markdown_fences()
    {
        const string text = "```json\n{\"a\": 1, \"b\": {\"c\": \"}\"}}\n```";
        Assert.Equal("{\"a\": 1, \"b\": {\"c\": \"}\"}}", JsonExtractor.ExtractFirstJsonObject(text).Trim());
    }

    [Fact]
    public void Extracts_from_surrounding_prose()
    {
        const string text = "Here is the plan you asked for:\n{\"x\": [1, 2],}\nThanks!";
        Assert.Contains("\"x\"", JsonExtractor.ExtractFirstJsonObject(text));
    }

    [Fact]
    public void Skips_balanced_prose_braces_before_valid_json()
    {
        const string text = "draft {not JSON}; final: {\"verdict\":\"PASS\"}";
        Assert.Equal("{\"verdict\":\"PASS\"}", JsonExtractor.ExtractFirstJsonObject(text));
    }

    [Fact]
    public void Skips_nested_json_inside_a_malformed_outer_region()
    {
        const string text = "draft {bad {\"mechanical_revert_sufficient\":true}} " +
                            "final: {\"mechanical_revert_sufficient\":false}";

        Assert.Equal("{\"mechanical_revert_sufficient\":false}",
            JsonExtractor.ExtractFirstJsonObject(text));
    }

    [Fact]
    public void Brace_heavy_unbalanced_input_fails_without_nested_rescans()
    {
        var text = new string('{', 100_000);

        Assert.Throws<InvalidOperationException>(() => JsonExtractor.ExtractFirstJsonObject(text));
    }

    [Fact]
    public void Unbalanced_json_throws()
    {
        Assert.Throws<InvalidOperationException>(() => JsonExtractor.ExtractFirstJsonObject("{\"a\": 1"));
    }

    [Fact]
    public void Empty_response_throws() =>
        Assert.Throws<InvalidOperationException>(() => JsonExtractor.ExtractFirstJsonObject("   "));
}
