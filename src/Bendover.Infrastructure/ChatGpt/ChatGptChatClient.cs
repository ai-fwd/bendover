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
        var sessionId = Guid.NewGuid().ToString("N");
        var attempt = await SendRequestAsync(
            session,
            sessionId,
            BuildResponsesRequest(messages, options, model),
            cancellationToken);
        if (!attempt.IsSuccess)
        {
            throw new InvalidOperationException($"ChatGPT request failed ({attempt.StatusCode}): {attempt.ResponseBody}");
        }

        return BuildCompletion(model, attempt.ResponseBody);
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

    private async Task<HttpAttemptResult> SendRequestAsync(
        ChatGptAuthSession session,
        string sessionId,
        object requestPayload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(requestPayload, JsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatGptDefaults.CodexResponsesEndpoint)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json))
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        httpRequest.Headers.UserAgent.ParseAdd("BendoverCLI/1.0");
        httpRequest.Headers.TryAddWithoutValidation("originator", ChatGptDefaults.Originator);
        httpRequest.Headers.TryAddWithoutValidation("session_id", sessionId);
        httpRequest.Headers.Accept.Clear();
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(session.AccountId))
        {
            httpRequest.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", session.AccountId);
        }

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new HttpAttemptResult((int)response.StatusCode, responseBody);
    }

    private static ChatCompletion BuildCompletion(string model, string responseBody)
    {
        var outputText = ExtractOutputText(responseBody);
        return new ChatCompletion(new ChatMessage(ChatRole.Assistant, outputText))
        {
            CreatedAt = DateTimeOffset.UtcNow,
            ModelId = model,
            FinishReason = ChatFinishReason.Stop,
            RawRepresentation = responseBody
        };
    }

    private static object BuildResponsesRequest(IList<ChatMessage> messages, ChatOptions? options, string model)
    {
        var instructions = BuildInstructions(messages);
        var input = messages
            .Where(message => message.Role != ChatRole.System)
            .Select(BuildResponsesInputMessage)
            .ToArray();

        return new
        {
            model,
            instructions = string.IsNullOrWhiteSpace(instructions) ? null : instructions,
            input,
            temperature = options?.Temperature,
            top_p = options?.TopP,
            max_output_tokens = options?.MaxOutputTokens,
            stream = true,
            store = false,
            stop = options?.StopSequences
        };
    }

    private static string BuildInstructions(IList<ChatMessage> messages)
    {
        return string.Join(
            "\n\n",
            messages
                .Where(message => message.Role == ChatRole.System)
                .Select(message => message.Text ?? string.Empty)
                .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static object BuildResponsesInputMessage(ChatMessage message)
    {
        var text = message.Text ?? string.Empty;

        if (message.Role == ChatRole.Assistant)
        {
            return new
            {
                role = "assistant",
                content = new[]
                {
                    new
                    {
                        type = "output_text",
                        text
                    }
                }
            };
        }

        return new
        {
            role = "user",
            content = new[]
            {
                new
                {
                    type = "input_text",
                    text
                }
            }
        };
    }

    private static string ExtractOutputText(string responseBody)
    {
        var eventStreamText = TryExtractOutputTextFromEventStream(responseBody);
        if (!string.IsNullOrWhiteSpace(eventStreamText))
        {
            return eventStreamText;
        }

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

    private static string? TryExtractOutputTextFromEventStream(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) || !responseBody.Contains("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var builder = new StringBuilder();
        var lines = responseBody.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var rawLine in lines)
        {
            if (!rawLine.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = rawLine.Substring("data:".Length).Trim();
            if (string.IsNullOrWhiteSpace(data) || data.Equals("[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var chunkDoc = JsonDocument.Parse(data);
                var chunk = chunkDoc.RootElement;
                if (!chunk.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var chunkType = typeProp.GetString();
                if (chunkType == "response.output_text.delta" &&
                    chunk.TryGetProperty("delta", out var deltaProp) &&
                    deltaProp.ValueKind == JsonValueKind.String)
                {
                    builder.Append(deltaProp.GetString());
                    continue;
                }

                if (chunkType == "response.output_item.added" &&
                    chunk.TryGetProperty("item", out var item) &&
                    item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("type", out var itemType) &&
                    itemType.ValueKind == JsonValueKind.String &&
                    itemType.GetString() == "message" &&
                    item.TryGetProperty("content", out var contentArray) &&
                    contentArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var contentPart in contentArray.EnumerateArray())
                    {
                        if (contentPart.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        if (contentPart.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                        {
                            builder.Append(partText.GetString());
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed event chunks and continue parsing.
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    private sealed record HttpAttemptResult(int StatusCode, string ResponseBody)
    {
        public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;
    }
}
