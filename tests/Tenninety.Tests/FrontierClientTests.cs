using System.Net;
using System.Text;
using System.Text.Json;
using Tenninety.Core.Models;
using Tenninety.Frontier;

namespace Tenninety.Tests;

public class FrontierClientTests
{
    [Fact]
    public async Task Pivot_wire_contract_binds_snake_case_fields()
    {
        const string content = """
            {
              "keep": ["WP-001"],
              "rework": [{
                "id": "WP-002",
                "reason": "requirements changed",
                "updated_directives": ["implement the revised rule"]
              }],
              "cancel": [],
              "new_work_packages": [{
                "id": "WP-900",
                "layer": "API",
                "module": "Core",
                "title": "New endpoint",
                "dependencies": ["WP-002"],
                "goal": "Expose the revised rule.",
                "directives": ["add the endpoint"],
                "acceptance_criteria": ["endpoint responds"],
                "notes": "",
                "status": "PENDING"
              }],
              "rationale": "test"
            }
            """;
        var client = CreateClient(content);

        var proposal = await client.ProposePivotAsync(new PivotRequest("spec", "plan", "intent", "audit"));

        Assert.Equal(["implement the revised rule"], proposal.Rework.Single().UpdatedDirectives);
        Assert.Equal("WP-900", Assert.Single(proposal.NewWorkPackages).Id);
    }

    [Fact]
    public async Task Revert_wire_contract_honours_explicit_false()
    {
        var client = CreateClient(
            "{\"analysis\":\"manual work required\",\"steps\":[],\"mechanical_revert_sufficient\":false}");

        var guidance = await client.ProposeRevertAsync(new RevertRequest("commit", "diff", "reason"));

        Assert.False(guidance.MechanicalRevertSufficient);
    }

    [Fact]
    public async Task Revert_wire_contract_requires_the_safety_flag()
    {
        var client = CreateClient("{\"analysis\":\"unspecified\",\"steps\":[]}");

        await Assert.ThrowsAsync<FrontierCallException>(
            () => client.ProposeRevertAsync(new RevertRequest("commit", "diff", "reason")));
    }

    [Fact]
    public async Task Revert_ignores_nested_approval_inside_malformed_prose()
    {
        var client = CreateClient(
            "draft {bad {\"mechanical_revert_sufficient\":true}} final " +
            "{\"analysis\":\"manual work required\",\"steps\":[],\"mechanical_revert_sufficient\":false}");

        var guidance = await client.ProposeRevertAsync(new RevertRequest("commit", "diff", "reason"));

        Assert.False(guidance.MechanicalRevertSufficient);
    }

    [Fact]
    public async Task Error_response_redacts_secrets()
    {
        const string secret = "supersecretvalue123";
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent($"{{\"client_secret\":\"{secret}\"}}"),
        });
        var client = CreateClient(handler);

        var ex = await Assert.ThrowsAsync<FrontierCallException>(
            () => client.ProposePivotAsync(new PivotRequest("spec", "plan", "intent", "audit")));
        Assert.DoesNotContain(secret, ex.Message);
    }

    private static HttpFrontierClient CreateClient(string content) =>
        CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Envelope(content), Encoding.UTF8, "application/json"),
        }));

    private static HttpFrontierClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), new TenNinetyConfig
        {
            ProviderMode = "aider",
            FrontierEndpoint = "http://frontier.test/v1",
            FrontierModel = "test-frontier",
        });

    private static string Envelope(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content } } },
    });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
