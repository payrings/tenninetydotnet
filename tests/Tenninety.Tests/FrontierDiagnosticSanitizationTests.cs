using System.Net;
using System.Text;
using Tenninety.Core.Models;
using Tenninety.Frontier;

namespace Tenninety.Tests;

/// <summary>
/// Every remote-body and model-parser diagnostic is SANITIZED and BOUNDED before it can
/// reach logs or the terminal: control characters are stripped (no terminal escapes), and
/// secret-shaped text is redacted even when bounding would otherwise cut its prefix.
/// Invalid Frontier plan output becomes a controlled FrontierCallException, never a raw
/// JsonException and never a silent default.
/// </summary>
public sealed class FrontierDiagnosticSanitizationTests
{
    private static HttpFrontierClient Client(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        return new(new HttpClient(handler), new TenNinetyConfig
        {
            ProviderMode = "aider",
            FrontierEndpoint = "http://frontier.test/v1",
            FrontierModel = "test-frontier",
        });
    }

    private static string Envelope(string content) => System.Text.Json.JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content } } },
    });

    [Fact]
    public void Diagnostic_builders_include_the_truncation_marker_within_the_exact_limit()
    {
        var raw = "\u001b[31m " + "sk-abcdefABCDEF0123456789 " +
                  new string('x', FrontierCallException.MaxDiagnosticChars * 3);

        var built = FrontierDiagnostics.Build(raw);
        var joined = FrontierDiagnostics.BuildJoined(" | ", [raw, raw]);

        Assert.Equal(FrontierCallException.MaxDiagnosticChars, built.Length);
        Assert.Equal(FrontierCallException.MaxDiagnosticChars, joined.Length);
        Assert.EndsWith("…", built);
        Assert.EndsWith("…", joined);
        Assert.DoesNotContain("\u001b", built);
        Assert.DoesNotContain("sk-abcdef", built);
    }

    // ---- HTTP error bodies ------------------------------------------------------------------

    [Fact]
    public async Task Http_error_bodies_are_sanitized_and_bounded()
    {
        var hostile =
            "\u001b]2;pwned\u0007" +                              // terminal escape (OSC)
            "sk-abcdefABCDEF0123456789 " +                        // secret-shaped token
            new string('x', 5_000);                               // over-long tail

        var ex = await Assert.ThrowsAsync<FrontierCallException>(
            () => Client(hostile, HttpStatusCode.InternalServerError).GetRepairAdviceAsync(
                new RepairRequest(new WorkPackage { Id = "WP-001" }, 1, [], null, "", "")));

        Assert.DoesNotContain("\u001b", ex.Message);
        Assert.DoesNotContain("sk-abcdef", ex.Message);
        Assert.Contains("[REDACTED", ex.Message);
        Assert.True(ex.Message.Length <= FrontierCallException.MaxDiagnosticChars + 64,
            "the diagnostic must stay bounded (prefix + ellipsis included)");
    }

    // ---- non-JSON success bodies (previously unsanitized) -------------------------------------

    [Fact]
    public async Task Non_json_bodies_are_sanitized_before_they_enter_the_exception()
    {
        var hostile = "<script>alert('xss')</script>\r\t\u0001" +
                      "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----\n" +
                      "trailing garbage for the bounded tail";

        var ex = await Assert.ThrowsAsync<FrontierCallException>(
            () => Client(hostile).GetRepairAdviceAsync(
                new RepairRequest(new WorkPackage { Id = "WP-001" }, 1, [], null, "", "")));

        Assert.Contains("frontier returned non-JSON body", ex.Message);
        Assert.Contains("<script>", ex.Message); // markup-like content is reported (readable)…
        Assert.DoesNotContain("-----BEGIN PRIVATE KEY-----", ex.Message); // …but never secrets
        Assert.DoesNotContain("\u0001", ex.Message);   // control characters are stripped
        Assert.DoesNotContain("\r", ex.Message);       // no carriage-return rewriting games
    }

    // ---- model-parser diagnostics ------------------------------------------------------------

    [Fact]
    public async Task Json_extractor_messages_contain_no_control_characters_or_secrets()
    {
        var content = "I cannot answer in JSON today. " +
                      "password=hunter2supersecret " +
                      "\u001b[31mredacted styling\u001b[0m";

        var ex = await Assert.ThrowsAsync<FrontierCallException>(
            () => Client(Envelope(content)).GetRepairAdviceAsync(
                new RepairRequest(new WorkPackage { Id = "WP-001" }, 1, [], null, "", "")));

        Assert.Contains("no JSON object found", ex.Message);
        Assert.DoesNotContain("\u001b", ex.Message);
        Assert.DoesNotContain("hunter2supersecret", ex.Message);
        Assert.Contains("[REDACTED", ex.Message);
    }

    [Fact]
    public async Task Invalid_frontier_plan_output_becomes_a_controlled_frontier_call_exception()
    {
        // A plan with an UNKNOWN member must fail at the strict parse boundary…
        var unknownMember = Envelope("{" +
            "\"schema_version\": \"1\", \"project_name\": \"P\", " +
            "\"work_packges\": []}");

        var ex1 = await Assert.ThrowsAsync<FrontierCallException>(
            () => Client(unknownMember).GeneratePlanAsync("spec"));

        Assert.Contains("failed to parse frontier JSON response", ex1.Message);
        Assert.Contains("frontier plan", ex1.Message);

        // …and a plan that parses but violates the blueprint rules fails validation.
        var invalidPlan = Envelope("{" +
            "\"schema_version\": \"9\", \"project_name\": \"\", " +
            "\"work_packages\": []}");
        var ex2 = await Assert.ThrowsAsync<FrontierCallException>(
            () => Client(invalidPlan).GeneratePlanAsync("spec"));

        Assert.Contains("frontier produced an invalid plan", ex2.Message);
        Assert.Contains("schema_version", ex2.Message);
    }

    [Fact]
    public async Task A_valid_frontier_plan_is_accepted_by_the_strict_boundary()
    {
        const string plan = """
            {
              "schema_version": "1",
              "project_name": "Demo",
              "global_context": { "tech_stack": "dotnet", "coding_standards": [], "assumptions": [] },
              "work_packages": [
                {
                  "id": "WP-001", "layer": "INFRA", "module": "Core", "title": "t",
                  "dependencies": [], "goal": "g", "directives": ["d"], "acceptance_criteria": [],
                  "notes": "", "status": "PENDING"
                }
              ]
            }
            """;

        var plan1 = await Client(Envelope(plan)).GeneratePlanAsync("spec");

        Assert.Equal("Demo", plan1.ProjectName);
        Assert.Equal("WP-001", Assert.Single(plan1.WorkPackages).Id);
    }

    // ---- hostile validation errors (the previously unsanitized path) --------------------------

    [Fact]
    public async Task Validation_errors_are_sanitized_bounded_and_redacted()
    {
        // A hostile work-package id: terminal escape, control characters, a secret-shaped
        // token and a 5000-character payload. The id never matches the blueprint pattern, so
        // PlanValidator echoes it into validation errors — which must still arrive sanitized
        // and bounded inside the controlled FrontierCallException.
        var hostileId = "\u001b]2;pwned\u0007\u0002" +
                        "<script>alert('x')</script>" +
                        "sk-abcdefABCDEF0123456789 " +
                        new string('x', 5_000);

        var plan = Envelope("{" +
            "\"schema_version\": \"1\", \"project_name\": \"P\", " +
            $"\"work_packages\": [{{ \"id\": \"{JsonEncode(hostileId)}\", \"layer\": \"INFRA\", " +
            "\"title\": \"t\", \"dependencies\": [], \"goal\": \"g\", " +
            "\"directives\": [\"d\"], \"acceptance_criteria\": [], \"status\": \"PENDING\" }]}");

        var ex = await Assert.ThrowsAsync<FrontierCallException>(
            () => Client(plan).GeneratePlanAsync("spec"));

        Assert.Contains("frontier produced an invalid plan", ex.Message);
        Assert.DoesNotContain("\u001b", ex.Message);            // terminal escapes stripped
        Assert.DoesNotContain("\u0007", ex.Message);             // BEL stripped
        Assert.DoesNotContain("\u0002", ex.Message);             // C0 control stripped
        Assert.DoesNotContain("sk-abcdef", ex.Message);          // secret-shaped value redacted
        Assert.Contains("[REDACTED", ex.Message);                // redaction marker present
        Assert.Contains("<script>", ex.Message);                 // markup stays readable…
        Assert.True(ex.Message.Length <=
                    "frontier produced an invalid plan: ".Length +
                    FrontierCallException.MaxDiagnosticChars + 16,
            "the joined validation diagnostic must stay within the configured bound");
    }

    [Fact]
    public async Task Extremely_long_dependency_ids_do_not_blow_past_the_diagnostic_bound()
    {
        var hostileDep = "sk-abcdefABCDEF0123456789" + new string('y', 5_000) + "\u001b[31m";
        var plan = Envelope("{" +
            "\"schema_version\": \"1\", \"project_name\": \"P\", " +
            "\"work_packages\": [" +
            "{ \"id\": \"WP-001\", \"layer\": \"INFRA\", \"title\": \"t\", " +
            $"\"dependencies\": [\"{JsonEncode(hostileDep)}\"], \"goal\": \"g\", " +
            "\"directives\": [\"d\"], \"acceptance_criteria\": [], \"status\": \"PENDING\" }]}");

        var ex = await Assert.ThrowsAsync<FrontierCallException>(
            () => Client(plan).GeneratePlanAsync("spec"));

        Assert.Contains("frontier produced an invalid plan", ex.Message);
        Assert.DoesNotContain("sk-abcdef", ex.Message);
        Assert.DoesNotContain("\u001b", ex.Message);
        Assert.True(ex.Message.Length <=
                    "frontier produced an invalid plan: ".Length +
                    FrontierCallException.MaxDiagnosticChars + 16);
    }

    /// <summary>JSON-escapes a raw string so it can be embedded in a hand-built JSON document
    /// (control characters included, exactly like a hostile model would emit them).</summary>
    private static string JsonEncode(string raw) =>
        System.Text.Json.JsonSerializer.Serialize(raw).Trim('"');

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
