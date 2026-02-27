using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bendover.Infrastructure.ChatGpt;
using Microsoft.Extensions.AI;

namespace Bendover.Tests;

public sealed class ChatGptOpenAiProxyServerTests
{
    [Fact]
    public async Task ChatCompletions_ShouldTranslateRequest_AndReturnOpenAiPayload()
    {
        using var auth = new AuthFixture(withSession: true);
        var recordingClient = new RecordingChatClient("proxy-ok");
        string? capturedModel = null;

        await using var harness = await ProxyHarness.StartAsync(
            auth.TokenProvider,
            model =>
            {
                capturedModel = model;
                return recordingClient;
            });

        var requestJson = """
            {
              "model":"openai/gpt-5.3-codex",
              "messages":[
                {"role":"system","content":"you are strict"},
                {"role":"user","content":"hello"}
              ],
              "temperature":0.2,
              "top_p":0.8,
              "max_tokens":111,
              "stop":["DONE"],
              "stream":false
            }
            """;

        var response = await harness.HttpClient.PostAsync(
            $"{harness.BaseUrl}/v1/chat/completions",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("openai/gpt-5.3-codex", capturedModel);

        var call = Assert.Single(recordingClient.Calls);
        Assert.Equal("you are strict", call.Messages[0].Text);
        Assert.Equal("hello", call.Messages[1].Text);
        Assert.Equal("openai/gpt-5.3-codex", call.Options?.ModelId);
        Assert.Equal(111, call.Options?.MaxOutputTokens);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("openai/gpt-5.3-codex", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("proxy-ok", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task ChatCompletions_ShouldReturn401_WhenSessionIsMissing()
    {
        using var auth = new AuthFixture(withSession: false);
        await using var harness = await ProxyHarness.StartAsync(auth.TokenProvider, _ => new RecordingChatClient("unused"));

        var requestJson = """
            {
              "model":"openai/gpt-5.3-codex",
              "messages":[{"role":"user","content":"hello"}]
            }
            """;

        var response = await harness.HttpClient.PostAsync(
            $"{harness.BaseUrl}/v1/chat/completions",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("authentication_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains("/connect", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatCompletions_ShouldMapUpstreamFailureStatus()
    {
        using var auth = new AuthFixture(withSession: true);
        await using var harness = await ProxyHarness.StartAsync(
            auth.TokenProvider,
            _ => new ThrowingChatClient(new InvalidOperationException("ChatGPT request failed (429): rate limited")));

        var requestJson = """
            {
              "model":"openai/gpt-5.3-codex",
              "messages":[{"role":"user","content":"hello"}]
            }
            """;

        var response = await harness.HttpClient.PostAsync(
            $"{harness.BaseUrl}/v1/chat/completions",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("upstream_error", doc.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains("rate limited", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ModelsEndpoint_ShouldReturnModelList()
    {
        using var auth = new AuthFixture(withSession: true);
        await using var harness = await ProxyHarness.StartAsync(auth.TokenProvider, _ => new RecordingChatClient("unused"));

        var response = await harness.HttpClient.GetAsync($"{harness.BaseUrl}/v1/models");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("list", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal(ChatGptDefaults.DefaultModel, doc.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ResponsesEndpoint_ShouldTranslateInstructionsAndInput()
    {
        using var auth = new AuthFixture(withSession: true);
        var recordingClient = new RecordingChatClient("response-ok");
        string? capturedModel = null;

        await using var harness = await ProxyHarness.StartAsync(
            auth.TokenProvider,
            model =>
            {
                capturedModel = model;
                return recordingClient;
            });

        var requestJson = """
            {
              "model":"openai/gpt-5.3-codex",
              "instructions":"be concise",
              "input":[
                {
                  "role":"user",
                  "content":[{"type":"input_text","text":"hi from responses"}]
                }
              ]
            }
            """;

        var response = await harness.HttpClient.PostAsync(
            $"{harness.BaseUrl}/v1/responses",
            new StringContent(requestJson, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("openai/gpt-5.3-codex", capturedModel);
        var call = Assert.Single(recordingClient.Calls);
        Assert.Equal("be concise", call.Messages[0].Text);
        Assert.Equal("hi from responses", call.Messages[1].Text);

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("response", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("response-ok", doc.RootElement.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Health_ShouldReturnOk()
    {
        using var auth = new AuthFixture(withSession: true);
        await using var harness = await ProxyHarness.StartAsync(auth.TokenProvider, _ => new RecordingChatClient("unused"));

        var response = await harness.HttpClient.GetAsync($"{harness.BaseUrl}/health");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    private sealed class ProxyHarness : IAsyncDisposable
    {
        private ProxyHarness(ChatGptOpenAiProxyServer server, string baseUrl)
        {
            Server = server;
            BaseUrl = baseUrl;
            HttpClient = new HttpClient();
        }

        public ChatGptOpenAiProxyServer Server { get; }
        public string BaseUrl { get; }
        public HttpClient HttpClient { get; }

        public static async Task<ProxyHarness> StartAsync(ChatGptTokenProvider tokenProvider, Func<string, IChatClient> chatClientFactory)
        {
            var port = GetAvailablePort();
            var prefix = $"http://127.0.0.1:{port}/";
            var server = new ChatGptOpenAiProxyServer(tokenProvider, chatClientFactory, prefix);
            server.Start();
            var harness = new ProxyHarness(server, $"http://127.0.0.1:{port}");
            await harness.WaitUntilHealthy();
            return harness;
        }

        public async ValueTask DisposeAsync()
        {
            HttpClient.Dispose();
            await Server.DisposeAsync();
        }

        private async Task WaitUntilHealthy()
        {
            for (var i = 0; i < 20; i++)
            {
                try
                {
                    var response = await HttpClient.GetAsync($"{BaseUrl}/health");
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch
                {
                    // Retry until listener is ready.
                }

                await Task.Delay(20);
            }

            throw new InvalidOperationException("Proxy server did not become healthy.");
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class AuthFixture : IDisposable
    {
        private readonly string _rootPath;
        public ChatGptTokenProvider TokenProvider { get; }

        public AuthFixture(bool withSession)
        {
            _rootPath = Path.Combine(Path.GetTempPath(), "bendover_proxy_tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_rootPath);
            var store = new ChatGptAuthStore(_rootPath);

            if (withSession)
            {
                store.Save(new ChatGptAuthSession(
                    AccessToken: "access-token",
                    RefreshToken: "refresh-token",
                    ExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
                    AccountId: "account-1",
                    Email: "user@example.com"));
            }

            var oauthClient = new ChatGptOAuthClient(new HttpClient(new NeverCalledHttpMessageHandler()));
            TokenProvider = new ChatGptTokenProvider(store, oauthClient);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_rootPath))
                {
                    Directory.Delete(_rootPath, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string _replyText;

        public RecordingChatClient(string replyText)
        {
            _replyText = replyText;
        }

        public ChatClientMetadata Metadata => new("proxy-test", new Uri("http://127.0.0.1"), "proxy-test-model");

        public List<(IList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = new();

        public T? GetService<T>(object? key = null) where T : class
        {
            return null;
        }

        public Task<ChatCompletion> CompleteAsync(IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((messages.ToList(), options));
            var completion = new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, _replyText) });
            return Task.FromResult(completion);
        }

        public IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        private readonly Exception _exception;

        public ThrowingChatClient(Exception exception)
        {
            _exception = exception;
        }

        public ChatClientMetadata Metadata => new("proxy-test", new Uri("http://127.0.0.1"), "proxy-test-model");

        public T? GetService<T>(object? key = null) where T : class
        {
            return null;
        }

        public Task<ChatCompletion> CompleteAsync(IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw _exception;
        }

        public IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(IList<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public void Dispose()
        {
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
