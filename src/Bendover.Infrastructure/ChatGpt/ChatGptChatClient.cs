using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Bendover.Infrastructure.ChatGpt;

public sealed class ChatGptChatClient : IChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ChatGptTokenProvider _tokenProvider;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _modelId;
    private readonly ChatClientMetadata _metadata;

    public ChatGptChatClient(ChatGptTokenProvider tokenProvider, string modelId, HttpClient? httpClient = null)
    {
        _tokenProvider = tokenProvider;
        _modelId = modelId;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();
        _metadata = new ChatClientMetadata("chatgpt-subscription", new Uri(ChatGptDefaults.CodexResponsesEndpoint), modelId);
    }

    public ChatClientMetadata Metadata => _metadata;

    public T? GetService<T>(object? key = null)
        where T : class
    {
        return null;
    }

    public async Task<ChatCompletion> CompleteAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var session = await _tokenProvider.GetValidSessionAsync(cancellationToken);
        if (session == null)
        {
            throw new InvalidOperationException("ChatGPT subscription is not connected. Run `/connect` to authorize.");
        }

        var model = options?.ModelId ?? _modelId;
        var request = BuildRequest(messages, options, model);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatGptDefaults.CodexResponsesEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        httpRequest.Headers.UserAgent.ParseAdd("BendoverCLI/1.0");
        httpRequest.Headers.TryAddWithoutValidation("originator", ChatGptDefaults.Originator);
        if (!string.IsNullOrWhiteSpace(session.AccountId))
        {
            httpRequest.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", session.AccountId);
        }
        httpRequest.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=2024-07-01");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"ChatGPT request failed ({(int)response.StatusCode}): {responseBody}");
        }

        var outputText = ExtractOutputText(responseBody);

        var completion = new ChatCompletion(new ChatMessage(ChatRole.Assistant, outputText))
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ModelId = model,
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = responseBody
        };

        return completion;
    }

    public IAsyncEnumerable<StreamingChatCompletionUpdate> CompleteStreamingAsync(
        IList<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Streaming responses are not supported for ChatGPT subscription mode.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private static object BuildRequest(IList<ChatMessage> messages, ChatOptions? options, string model)
    {
        var input = messages.Select(message => new
        {
            role = MapRole(message.Role),
            content = new[]
            {
                new
                {
                    type = "text",
                    text = message.Text ?? string.Empty
                }
            }
        });

        return new
        {
            model,
            input,
            temperature = options?.Temperature,
            top_p = options?.TopP,
            max_output_tokens = options?.MaxOutputTokens,
            stop = options?.StopSequences
        };
    }

    private static string MapRole(ChatRole role)
    {
        if (role == ChatRole.System)
        {
            return "system";
        }

        if (role == ChatRole.Assistant)
        {
            return "assistant";
        }

        if (role == ChatRole.Tool)
        {
            return "tool";
        }

        return "user";
    }

    private static string ExtractOutputText(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            {
                return outputText.GetString() ?? string.Empty;
            }

            if (root.TryGetProperty("output", out var outputArray) && outputArray.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var item in outputArray.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "message")
                    {
                        if (item.TryGetProperty("content", out var contentArray) && contentArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var contentItem in contentArray.EnumerateArray())
                            {
                                if (contentItem.TryGetProperty("text", out var textProp))
                                {
                                    builder.Append(textProp.GetString());
                                }
                            }
                        }
                    }
                }

                if (builder.Length > 0)
                {
                    return builder.ToString();
                }
            }
        }
        catch
        {
            return responseBody;
        }

        return responseBody;
    }
}
