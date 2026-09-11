using System.Net;
using System.Text;
using System.Text.Json;
using Tenninety.Core.Models;
using Tenninety.Frontier;

namespace Tenninety.Tests;

public class FrontierClientTests
{
    public static TheoryData<string> InvalidRepairResponses() => new()
    {
        "{\"analysis\":\"diagnosis\",\"advice\":null}",
        "{}",
        "{\"analysis\":null,\"advice\":[\"act\"]}",
        "{\"analysis\":\"diagnosis\"}",
        "{\"advice\":[\"act\"]}",
        "{\"analysis\":\"diagnosis\",\"advice\":[null]}",
        "{\"analysis\":\"diagnosis\",\"advice\":[]}",
        "{\"analysis\":\"   \",\"advice\":[\"act\"]}",
        "{\"analysis\":\"first\",\"analysis\":\"second\",\"advice\":[\"act\"]}",
        "{\"analysis\":\"diagnosis\",\"advice\":[\"act\"],\"unexpected\":true}",
        JsonSerializer.Serialize(new
        {
            analysis = new string('x', 4001),
            advice = new[] { "act" },
        }),
    };

    [Theory]
    [MemberData(nameof(InvalidRepairResponses))]
    public async Task Repair_wire_contract_rejects_malformed_or_unusable_valid_json(string content)
    {
        var client = CreateClient(content);

        var error = await Assert.ThrowsAsync<FrontierCallException>(() =>
            client.GetRepairAdviceAsync(RepairRequest()));

        Assert.Contains("failed to parse frontier JSON response", error.Message);
    }

    [Fact]
    public async Task Repair_wire_contract_accepts_complete_actionable_advice()
    {
        var client = CreateClient(
            "{\"analysis\":\"dependency mismatch\",\"advice\":[\"pin the expected version\"]}");

        var result = await client.GetRepairAdviceAsync(RepairRequest());

        Assert.Equal("dependency mismatch", result.Analysis);
        Assert.Equal(["pin the expected version"], result.Advice);
    }

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

    [Fact]
    public async Task Body_stall_hits_the_request_deadline_and_disposes_without_a_leaked_read()
    {
        var stream = new BlockingReadStream();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        }, token => stream.SendToken = token);
        var client = CreateClient(handler, TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<FrontierCallException>(() =>
            client.ProposePivotAsync(new PivotRequest("spec", "plan", "intent", "audit"))
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
        await stream.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stream.SendToken.CanBeCanceled);
        Assert.True(stream.ReadToken.CanBeCanceled);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_classified_as_a_frontier_timeout()
    {
        var stream = new BlockingReadStream();
        var client = CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        }), TimeSpan.FromSeconds(5));
        using var caller = new CancellationTokenSource();
        var request = client.ProposePivotAsync(
            new PivotRequest("spec", "plan", "intent", "audit"), caller.Token);
        await stream.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request.WaitAsync(TimeSpan.FromSeconds(5)));
        await stream.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Normal_response_completes_with_the_injected_request_deadline()
    {
        var client = CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                Envelope("{\"analysis\":\"ok\",\"steps\":[],\"mechanical_revert_sufficient\":false}"),
                Encoding.UTF8, "application/json"),
        }), TimeSpan.FromSeconds(1));

        var result = await client.ProposeRevertAsync(
            new RevertRequest("commit", "diff", "reason"));

        Assert.Equal("ok", result.Analysis);
        Assert.False(result.MechanicalRevertSufficient);
    }

    [Fact]
    public async Task Oversized_response_keeps_the_size_error_and_disposes_content()
    {
        var content = new DeclaredOversizedContent();
        var client = CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }), TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<FrontierCallException>(() =>
            client.ProposePivotAsync(new PivotRequest("spec", "plan", "intent", "audit")));

        Assert.Equal("frontier response exceeded the 4 MiB limit.", error.Message);
        Assert.False(content.StreamRequested);
        Assert.True(content.Disposed);
    }

    private static HttpFrontierClient CreateClient(string content) =>
        CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Envelope(content), Encoding.UTF8, "application/json"),
        }));

    private static HttpFrontierClient CreateClient(
        HttpMessageHandler handler, TimeSpan? requestTimeout = null)
    {
        var config = new TenNinetyConfig
        {
            ProviderMode = "aider",
            FrontierEndpoint = "http://frontier.test/v1",
            FrontierModel = "test-frontier",
        };
        var http = new HttpClient(handler);
        return requestTimeout is { } timeout
            ? new HttpFrontierClient(http, config, timeout)
            : new HttpFrontierClient(http, config);
    }

    private static string Envelope(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { role = "assistant", content } } },
    });

    private static RepairRequest RepairRequest() => new(
        TestPlans.Wp("WP-001"), 3, ["failure"], null, "audit", "diff");

    private sealed class StubHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        Action<CancellationToken>? observeToken = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            observeToken?.Invoke(cancellationToken);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken SendToken { get; set; }
        public CancellationToken ReadToken { get; private set; }
        public bool Disposed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WaitForCancellationAsync(cancellationToken);

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            ReadToken = cancellationToken;
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            finally
            {
                ReadCompleted.TrySetResult();
            }
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class DeclaredOversizedContent : HttpContent
    {
        public bool StreamRequested { get; private set; }
        public bool Disposed { get; private set; }

        public DeclaredOversizedContent() =>
            Headers.ContentLength = 4L * 1024 * 1024 + 1;

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context)
        {
            StreamRequested = true;
            throw new InvalidOperationException("oversized content must not be read");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 4L * 1024 * 1024 + 1;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
