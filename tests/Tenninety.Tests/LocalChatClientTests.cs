using System.Net;
using System.Text;
using Tenninety.Execution.OpenAi;

namespace Tenninety.Tests;

public sealed class LocalChatClientTests
{
    [Fact]
    public async Task CompleteAsync_returns_only_bounded_message_content()
    {
        var handler = new StubHandler(_ => JsonResponse("bounded"));
        var client = new LocalChatClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-model/v1/"),
        });

        var result = await client.CompleteAsync("reviewer", "system", "user", 7, default);

        Assert.Equal("bounded", result);
    }

    [Fact]
    public async Task Declared_oversized_transport_body_is_refused_before_it_is_buffered()
    {
        var content = new TrackingContent(new byte[100_000]);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        });
        var client = new LocalChatClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-model/v1/"),
        });

        await Assert.ThrowsAsync<ChatResponseLimitExceededException>(() =>
            client.CompleteAsync("reviewer", "system", "user", 100, default));

        Assert.False(content.StreamRequested);
        Assert.False(content.Serialized);
    }

    [Fact]
    public async Task Oversized_message_content_is_rejected_even_inside_a_bounded_envelope()
    {
        var handler = new StubHandler(_ => JsonResponse(new string('x', 101)));
        var client = new LocalChatClient(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://local-model/v1/"),
        });

        await Assert.ThrowsAsync<ChatResponseLimitExceededException>(() =>
            client.CompleteAsync("reviewer", "system", "user", 100, default));
    }

    private static HttpResponseMessage JsonResponse(string message) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"choices\":[{\"message\":{\"content\":" +
            System.Text.Json.JsonSerializer.Serialize(message) + "}}]}",
            Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class TrackingContent(byte[] bytes) : HttpContent
    {
        public bool StreamRequested { get; private set; }
        public bool Serialized { get; private set; }

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context)
        {
            Serialized = true;
            await stream.WriteAsync(bytes);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = bytes.Length;
            return true;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StreamRequested = true;
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }
}
