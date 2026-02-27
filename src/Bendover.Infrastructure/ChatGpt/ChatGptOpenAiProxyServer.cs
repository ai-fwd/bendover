using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Bendover.Infrastructure.ChatGpt;

public sealed class ChatGptOpenAiProxyServer : IAsyncDisposable
{
    public const string DefaultPrefix = "http://127.0.0.1:11434/";
    public const string DefaultApiBase = "http://127.0.0.1:11434/v1";
    private const string DefaultSystemPrompt = "You are a careful assistant. Follow the user\u2019s instructions exactly. Output only what the user asked for, in the requested format.";

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ChatGptTokenProvider _tokenProvider;
    private readonly Func<string, IChatClient> _chatClientFactory;
    private readonly Action<string>? _log;
    private readonly HttpListener _listener;
    private readonly string _prefix;

    private CancellationTokenSource? _cts;
    private Task? _serveTask;
    private bool _started;
    private bool _disposed;

    public ChatGptOpenAiProxyServer(
        ChatGptTokenProvider tokenProvider,
        Func<string, IChatClient>? chatClientFactory = null,
        string? prefix = null,
        Action<string>? log = null)
    {
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _chatClientFactory = chatClientFactory ?? (model => new ChatGptChatClient(_tokenProvider, model));
        _log = log;
        _prefix = NormalizePrefix(prefix ?? DefaultPrefix);
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        BaseApiUrl = BuildBaseApiUrl(_prefix);
    }

    public string Prefix => _prefix;
    public string BaseApiUrl { get; }

    public void Start(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_started)
        {
            throw new InvalidOperationException("Proxy server is already started.");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _serveTask = Task.Run(() => ServeLoopAsync(_cts.Token), CancellationToken.None);
        _started = true;
    }

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _cts?.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Best effort.
        }

        if (_serveTask != null)
        {
            try
            {
                await _serveTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore shutdown races from HttpListener.
            }
        }

        _cts?.Dispose();
        _cts = null;
        _serveTask = null;
        _started = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _disposed = true;
    }

    private async Task ServeLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            if (context == null)
            {
                continue;
            }

            _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var method = context.Request.HttpMethod?.ToUpperInvariant() ?? string.Empty;
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            var traceId = Guid.NewGuid().ToString("N")[..8];
            Log(traceId, $"Incoming request: {method} {path}");

            if (method == "GET" && string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, 200, new { status = "ok" }, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (method == "GET" && string.Equals(path, "/v1/models", StringComparison.OrdinalIgnoreCase))
            {
                var payload = new
                {
                    @object = "list",
                    data = new[]
                    {
                        new
                        {
                            id = ChatGptDefaults.DefaultModel,
                            @object = "model",
                            created = 0,
                            owned_by = "openai"
                        }
                    }
                };
                await WriteJsonAsync(context.Response, 200, payload, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && string.Equals(path, "/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                await HandleChatCompletionsAsync(context, traceId, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (method == "POST" && string.Equals(path, "/v1/responses", StringComparison.OrdinalIgnoreCase))
            {
                await HandleResponsesAsync(context, traceId, cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteErrorAsync(context.Response, 404, "not_found", "Endpoint not found.", cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(
                context.Response,
                400,
                "invalid_request_error",
                $"Invalid JSON payload: {ex.Message}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("proxy", $"Unhandled request error: {ex.Message}");
            await WriteErrorAsync(
                context.Response,
                500,
                "internal_error",
                $"Proxy request failed: {ex.Message}",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                context.Response.OutputStream.Close();
            }
            catch
            {
                // Best effort.
            }
        }
    }

    private async Task HandleChatCompletionsAsync(HttpListenerContext context, string traceId, CancellationToken cancellationToken)
    {
        if (_tokenProvider.LoadSession() is null)
        {
            await WriteErrorAsync(
                context.Response,
                401,
                "authentication_error",
                "ChatGPT subscription is not connected. Run `/connect` to authorize.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        Log(traceId, "Incoming JSON:");
        Log(traceId, TruncateForLog(body, 8000));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (IsStreamingRequested(root))
        {
            await WriteErrorAsync(
                context.Response,
                400,
                "invalid_request_error",
                "Streaming is not supported by ChatGPT subscription proxy.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var model = GetString(root, "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            await WriteErrorAsync(
                context.Response,
                400,
                "invalid_request_error",
                "chat.completions request must include a non-empty model.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var messages = ParseChatMessages(root);
        if (messages.Count == 0)
        {
            await WriteErrorAsync(
                context.Response,
                400,
                "invalid_request_error",
                "chat.completions request must include at least one message.",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        messages = EnsureSystemInstruction(messages, out var chatPromptInjected);
        if (chatPromptInjected)
        {
            Log(traceId, "No system/developer instruction detected. Injected default system prompt.");
        }

        var options = BuildChatOptions(root, model);
        Log(traceId, "Mapped request:");
        Log(traceId, $"model={model}");
        Log(traceId, $"messages:\n{FormatMessagesForLog(messages)}");
        Log(traceId, $"options={FormatOptionsForLog(options)}");

        string outputText;
        try
        {
            outputText = await CompleteAsync(model, messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ProxyHttpException ex)
        {
            Log(traceId, $"Upstream error ({ex.StatusCode}): {ex.Detail}");
            var errorType = ex.StatusCode == 401 ? "authentication_error" : "upstream_error";
            await WriteErrorAsync(context.Response, ex.StatusCode, errorType, ex.Detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        Log(traceId, $"Result:\n{TruncateForLog(outputText, 8000)}");

        var payload = new
        {
            id = $"chatcmpl_{Guid.NewGuid():N}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = outputText
                    },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            }
        };

        await WriteJsonAsync(context.Response, 200, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleResponsesAsync(HttpListenerContext context, string traceId, CancellationToken cancellationToken)
    {
        if (_tokenProvider.LoadSession() is null)
        {
            await WriteErrorAsync(
                context.Response,
                401,
                "authentication_error",
                "ChatGPT subscription is not connected. Run `/connect` to authorize.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var body = await ReadBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        Log(traceId, "Incoming JSON:");
        Log(traceId, TruncateForLog(body, 8000));
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (IsStreamingRequested(root))
        {
            await WriteErrorAsync(
                context.Response,
                400,
                "invalid_request_error",
                "Streaming is not supported by ChatGPT subscription proxy.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var model = GetString(root, "model");
        if (string.IsNullOrWhiteSpace(model))
        {
            await WriteErrorAsync(
                context.Response,
                400,
                "invalid_request_error",
                "responses request must include a non-empty model.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var messages = ParseResponsesInput(root);
        if (messages.Count == 0)
        {
            await WriteErrorAsync(
                context.Response,
                400,
                "invalid_request_error",
                "responses request must include instructions or input.",
                cancellationToken).ConfigureAwait(false);
            return;
        }
        messages = EnsureSystemInstruction(messages, out var responsesPromptInjected);
        if (responsesPromptInjected)
        {
            Log(traceId, "No system/developer instruction detected. Injected default system prompt.");
        }

        var options = BuildChatOptions(root, model);
        Log(traceId, "Mapped request:");
        Log(traceId, $"model={model}");
        Log(traceId, $"messages:\n{FormatMessagesForLog(messages)}");
        Log(traceId, $"options={FormatOptionsForLog(options)}");

        string outputText;
        try
        {
            outputText = await CompleteAsync(model, messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (ProxyHttpException ex)
        {
            Log(traceId, $"Upstream error ({ex.StatusCode}): {ex.Detail}");
            var errorType = ex.StatusCode == 401 ? "authentication_error" : "upstream_error";
            await WriteErrorAsync(context.Response, ex.StatusCode, errorType, ex.Detail, cancellationToken).ConfigureAwait(false);
            return;
        }

        Log(traceId, $"Result:\n{TruncateForLog(outputText, 8000)}");

        var messageId = $"msg_{Guid.NewGuid():N}";
        var payload = new
        {
            id = $"resp_{Guid.NewGuid():N}",
            @object = "response",
            created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            status = "completed",
            model,
            output_text = outputText,
            output = new[]
            {
                new
                {
                    type = "message",
                    id = messageId,
                    status = "completed",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = outputText
                        }
                    }
                }
            },
            usage = new
            {
                input_tokens = 0,
                output_tokens = 0,
                total_tokens = 0
            }
        };

        await WriteJsonAsync(context.Response, 200, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CompleteAsync(
        string model,
        IList<ChatMessage> messages,
        ChatOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            using var chatClient = _chatClientFactory(model);
            var completion = await chatClient.CompleteAsync(messages, options, cancellationToken).ConfigureAwait(false);
            return completion.Message.Text ?? string.Empty;
        }
        catch (InvalidOperationException ex) when (TryParseUpstreamFailure(ex.Message, out var statusCode, out var detail))
        {
            throw new ProxyHttpException(statusCode, detail);
        }
        catch (InvalidOperationException ex) when (LooksLikeAuthenticationFailure(ex.Message))
        {
            throw new ProxyHttpException(401, "ChatGPT subscription authentication failed. Reconnect with `/connect`.");
        }
    }

    private static bool LooksLikeAuthenticationFailure(string message)
    {
        return message.Contains("not connected", StringComparison.OrdinalIgnoreCase)
               || message.Contains("Token refresh failed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseUpstreamFailure(string message, out int statusCode, out string detail)
    {
        const string prefix = "ChatGPT request failed (";
        if (!message.StartsWith(prefix, StringComparison.Ordinal))
        {
            statusCode = 0;
            detail = string.Empty;
            return false;
        }

        var close = message.IndexOf(')', prefix.Length);
        if (close <= prefix.Length)
        {
            statusCode = 0;
            detail = string.Empty;
            return false;
        }

        if (!int.TryParse(message[prefix.Length..close], out statusCode))
        {
            statusCode = 0;
            detail = string.Empty;
            return false;
        }

        detail = message[(close + 1)..].TrimStart(':', ' ');
        return true;
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8, leaveOpen: false);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return body;
    }

    private static IList<ChatMessage> ParseChatMessages(JsonElement root)
    {
        var messages = new List<ChatMessage>();
        if (!root.TryGetProperty("messages", out var messagesElement) || messagesElement.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var item in messagesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = GetString(item, "role");
            var messageText = item.TryGetProperty("content", out var content)
                ? ExtractText(content)
                : string.Empty;
            messages.Add(new ChatMessage(ParseRole(role), messageText));
        }

        return messages;
    }

    private static IList<ChatMessage> ParseResponsesInput(JsonElement root)
    {
        var messages = new List<ChatMessage>();
        var instructions = GetString(root, "instructions");
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            messages.Add(new ChatMessage(ChatRole.System, instructions));
        }

        if (!root.TryGetProperty("input", out var input))
        {
            return messages;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            var text = input.GetString() ?? string.Empty;
            messages.Add(new ChatMessage(ChatRole.User, text));
            return messages;
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var inputItem in input.EnumerateArray())
        {
            if (inputItem.ValueKind == JsonValueKind.String)
            {
                messages.Add(new ChatMessage(ChatRole.User, inputItem.GetString() ?? string.Empty));
                continue;
            }

            if (inputItem.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = ParseRole(GetString(inputItem, "role"));
            if (inputItem.TryGetProperty("content", out var content))
            {
                messages.Add(new ChatMessage(role, ExtractText(content)));
                continue;
            }

            if (inputItem.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                messages.Add(new ChatMessage(role, textElement.GetString() ?? string.Empty));
            }
        }

        return messages;
    }

    private static ChatOptions BuildChatOptions(JsonElement root, string model)
    {
        var options = new ChatOptions
        {
            ModelId = model
        };

        if (TryGetNumber(root, "temperature", out var temperature))
        {
            options.Temperature = (float)temperature;
        }

        if (TryGetNumber(root, "top_p", out var topP))
        {
            options.TopP = (float)topP;
        }

        if (TryGetInteger(root, "max_tokens", out var maxTokens)
            || TryGetInteger(root, "max_output_tokens", out maxTokens)
            || TryGetInteger(root, "max_completion_tokens", out maxTokens))
        {
            options.MaxOutputTokens = (int)maxTokens;
        }

        var stop = ExtractStopSequences(root);
        if (stop.Count > 0)
        {
            options.StopSequences = stop;
        }

        return options;
    }

    private static List<string> ExtractStopSequences(JsonElement root)
    {
        var stopSequences = new List<string>();
        if (!root.TryGetProperty("stop", out var stopElement))
        {
            return stopSequences;
        }

        if (stopElement.ValueKind == JsonValueKind.String)
        {
            var value = stopElement.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                stopSequences.Add(value);
            }
            return stopSequences;
        }

        if (stopElement.ValueKind != JsonValueKind.Array)
        {
            return stopSequences;
        }

        foreach (var item in stopElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                stopSequences.Add(value);
            }
        }

        return stopSequences;
    }

    private static bool IsStreamingRequested(JsonElement root)
    {
        return root.TryGetProperty("stream", out var streamElement)
               && streamElement.ValueKind == JsonValueKind.True;
    }

    private static ChatRole ParseRole(string? role)
    {
        return role?.Trim().ToLowerInvariant() switch
        {
            "assistant" => ChatRole.Assistant,
            "system" => ChatRole.System,
            "developer" => ChatRole.System,
            _ => ChatRole.User
        };
    }

    private static string ExtractText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                return textElement.GetString() ?? string.Empty;
            }

            if (content.TryGetProperty("content", out var nested))
            {
                return ExtractText(nested);
            }

            return string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                builder.Append(item.GetString());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                builder.Append(textElement.GetString());
                continue;
            }

            if (item.TryGetProperty("content", out var nested))
            {
                builder.Append(ExtractText(nested));
            }
        }

        return builder.ToString();
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static bool TryGetNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var node)
               && node.ValueKind == JsonValueKind.Number
               && node.TryGetDouble(out value);
    }

    private static bool TryGetInteger(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var node)
               && node.ValueKind == JsonValueKind.Number
               && node.TryGetInt64(out value);
    }

    private static IList<ChatMessage> EnsureSystemInstruction(IList<ChatMessage> messages, out bool injected)
    {
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            if (message.Role == ChatRole.System && !string.IsNullOrWhiteSpace(message.Text))
            {
                injected = false;
                return messages;
            }
        }

        injected = true;
        var withSystemPrompt = new List<ChatMessage>(messages.Count + 1)
        {
            new ChatMessage(ChatRole.System, DefaultSystemPrompt)
        };
        for (var i = 0; i < messages.Count; i++)
        {
            withSystemPrompt.Add(messages[i]);
        }

        return withSystemPrompt;
    }

    private static async Task WriteErrorAsync(
        HttpListenerResponse response,
        int statusCode,
        string type,
        string message,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            error = new
            {
                message,
                type,
                code = statusCode
            }
        };
        await WriteJsonAsync(response, statusCode, payload, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        HttpListenerResponse response,
        int statusCode,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizePrefix(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("prefix is required", nameof(prefix));
        }

        var trimmed = prefix.Trim();
        if (!trimmed.EndsWith("/", StringComparison.Ordinal))
        {
            trimmed += "/";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid proxy prefix '{prefix}'.", nameof(prefix));
        }

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Proxy prefix must use http scheme.", nameof(prefix));
        }

        return trimmed;
    }

    private static string BuildBaseApiUrl(string prefix)
    {
        var uri = new Uri(prefix);
        var builder = new UriBuilder(uri)
        {
            Path = "/v1"
        };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ChatGptOpenAiProxyServer));
        }
    }

    private void Log(string traceId, string message)
    {
        _log?.Invoke($"[proxy][{DateTime.UtcNow:O}][{traceId}] {message}");
    }

    private static string FormatMessagesForLog(IList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return "(none)";
        }

        var lines = new List<string>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            var role = message.Role.ToString();
            var text = message.Text ?? string.Empty;
            lines.Add($"[{i}] role={role} text={TruncateForLog(text, 1200)}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatOptionsForLog(ChatOptions options)
    {
        var stop = options.StopSequences is { Count: > 0 }
            ? string.Join(", ", options.StopSequences)
            : "(none)";

        return $"model={options.ModelId}, temp={options.Temperature?.ToString() ?? "(null)"}, top_p={options.TopP?.ToString() ?? "(null)"}, max_tokens={options.MaxOutputTokens?.ToString() ?? "(null)"}, stop={stop}";
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...(truncated)";
    }

    private sealed class ProxyHttpException : Exception
    {
        public ProxyHttpException(int statusCode, string detail)
            : base(detail)
        {
            StatusCode = statusCode;
            Detail = detail;
        }

        public int StatusCode { get; }
        public string Detail { get; }
    }
}
