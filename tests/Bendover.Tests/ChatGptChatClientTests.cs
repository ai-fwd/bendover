using System.Net;
using System.Text;
using System.Text.Json;
using Bendover.Infrastructure.ChatGpt;
using Microsoft.Extensions.AI;

namespace Bendover.Tests;

public sealed class ChatGptChatClientTests
{
    [Fact]
    public async Task CompleteAsync_ShouldBuildInstructionsAndInputPayload()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => CreateResponse(HttpStatusCode.OK, """{"output_text":"ok"}"""));
        using var fixture = new ChatGptAuthFixture();
        using var httpClient = new HttpClient(handler);
        using var client = new ChatGptChatClient(fixture.TokenProvider, "gpt-5.2", httpClient);

        var completion = await client.CompleteAsync(new List<ChatMessage>
        {
            new(ChatRole.System, "You are the Lead Agent. Select relevant practice names as JSON array."),
            new(ChatRole.User, "test")
        });

        Assert.Equal("ok", completion.Message.Text);
        Assert.Single(handler.Requests);

        var request = handler.Requests[0];
        Assert.True(request.Headers.ContainsKey("session_id"));
        Assert.True(request.Headers.ContainsKey("ChatGPT-Account-Id"));
        Assert.True(request.Headers.ContainsKey("Content-Type"));
        Assert.Contains("application/json", request.Headers["Content-Type"]);
        Assert.DoesNotContain("charset", string.Join(";", request.Headers["Content-Type"]), StringComparison.OrdinalIgnoreCase);
        Assert.True(request.Headers.ContainsKey("Accept"));
        Assert.Contains("text/event-stream", request.Headers["Accept"]);

        using var doc = JsonDocument.Parse(request.Body);
        var root = doc.RootElement;
        Assert.Equal("gpt-5.2", root.GetProperty("model").GetString());
        Assert.Equal("You are the Lead Agent. Select relevant practice names as JSON array.", root.GetProperty("instructions").GetString());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.False(root.GetProperty("store").GetBoolean());

        var input = root.GetProperty("input");
        Assert.Equal(1, input.GetArrayLength());
        Assert.Equal("user", input[0].GetProperty("role").GetString());
        Assert.Equal("input_text", input[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("test", input[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task CompleteAsync_ShouldParseEventStreamDeltaResponse()
    {
        var sseBody = string.Join('\n', new[]
        {
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\" world\"}",
            "data: [DONE]",
            string.Empty
        });

        var handler = new SequenceHttpMessageHandler(
            _ => CreateResponse(HttpStatusCode.OK, sseBody, "text/event-stream"));
        using var fixture = new ChatGptAuthFixture();
        using var httpClient = new HttpClient(handler);
        using var client = new ChatGptChatClient(fixture.TokenProvider, "gpt-5.2", httpClient);

        var completion = await client.CompleteAsync(new List<ChatMessage>
        {
            new(ChatRole.System, "system"),
            new(ChatRole.User, "hello")
        });

        Assert.Equal("Hello world", completion.Message.Text);
    }

    [Fact]
    public async Task CompleteAsync_ShouldThrow_WhenRequestFails()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => CreateResponse(HttpStatusCode.BadRequest, """{"detail":"Unsupported content type"}"""));
        using var fixture = new ChatGptAuthFixture();
        using var httpClient = new HttpClient(handler);
        using var client = new ChatGptChatClient(fixture.TokenProvider, "gpt-5.2", httpClient);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.CompleteAsync(new List<ChatMessage>
            {
                new(ChatRole.System, "system"),
                new(ChatRole.User, "hello")
            }));

        Assert.Contains("ChatGPT request failed (400)", ex.Message);
    }

    [Fact]
    public async Task CompleteAsync_ShouldCombineMultipleSystemMessagesIntoInstructions()
    {
        var handler = new SequenceHttpMessageHandler(
            _ => CreateResponse(HttpStatusCode.OK, """{"output_text":"ok"}"""));
        using var fixture = new ChatGptAuthFixture();
        using var httpClient = new HttpClient(handler);
        using var client = new ChatGptChatClient(fixture.TokenProvider, "gpt-5.2", httpClient);

        await client.CompleteAsync(new List<ChatMessage>
        {
            new(ChatRole.System, "first"),
            new(ChatRole.System, "second"),
            new(ChatRole.User, "hello")
        });

        using var doc = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal("first\n\nsecond", doc.RootElement.GetProperty("instructions").GetString());
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, string body, string mediaType = "application/json")
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        };
    }

    private sealed class SequenceHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses;

        public SequenceHttpMessageHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses)
        {
            _responses = new Queue<Func<HttpRequestMessage, HttpResponseMessage>>(responses);
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = header.Value.ToArray();
            }

            if (request.Content != null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = header.Value.ToArray();
                }
            }

            Requests.Add(new CapturedRequest(body, headers));

            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response was configured.");
            }

            return _responses.Dequeue().Invoke(request);
        }
    }

    private sealed record CapturedRequest(string Body, IReadOnlyDictionary<string, string[]> Headers);

    private sealed class ChatGptAuthFixture : IDisposable
    {
        private readonly string _rootPath;
        public ChatGptTokenProvider TokenProvider { get; }

        public ChatGptAuthFixture()
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "bendover-tests", Guid.NewGuid().ToString("N"));
            var store = new ChatGptAuthStore(_rootPath);
            store.Save(new ChatGptAuthSession(
                AccessToken: "access-token",
                RefreshToken: "refresh-token",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                AccountId: "account-1",
                Email: "user@example.com"));

            var oauthClient = new ChatGptOAuthClient(new HttpClient(new NeverCalledHttpMessageHandler()));
            TokenProvider = new ChatGptTokenProvider(store, oauthClient);
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, true);
            }
        }
    }

    private sealed class NeverCalledHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("OAuth refresh should not be called in this test.");
        }
    }
}
