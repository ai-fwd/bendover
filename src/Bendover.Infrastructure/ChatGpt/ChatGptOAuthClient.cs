using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Sockets;

namespace Bendover.Infrastructure.ChatGpt;

public sealed class ChatGptOAuthClient
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public ChatGptOAuthClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<ChatGptAuthSession> AuthorizeAsync(Func<Uri, Task> openBrowser, CancellationToken cancellationToken)
    {
        var pkce = PkceCodes.Create();
        var state = GenerateState();
        using var listener = new OAuthListener();
        listener.SetExpectedState(state);
        var authUrl = BuildAuthorizeUrl(listener.RedirectUri, pkce, state);

        await openBrowser(authUrl);

        var code = await listener.WaitForAuthorizationCodeAsync(state, cancellationToken);
        var tokenResponse = await ExchangeCodeForTokensAsync(code, listener.RedirectUri, pkce, cancellationToken);
        var accountId = TryExtractAccountId(tokenResponse.IdToken);

        var expiresAt = tokenResponse.ExpiresIn.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
            : (DateTimeOffset?)null;

        return new ChatGptAuthSession(
            tokenResponse.AccessToken,
            tokenResponse.RefreshToken,
            expiresAt,
            accountId,
            null);
    }

    public async Task<ChatGptAuthSession> RefreshAsync(ChatGptAuthSession session, CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"{ChatGptDefaults.Issuer}/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
                ["client_id"] = ChatGptDefaults.ClientId
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Token refresh failed ({(int)response.StatusCode}): {body}");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(payload, JsonOptions)
                            ?? throw new InvalidOperationException("Token refresh response was empty.");
        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken) || string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
        {
            throw new InvalidOperationException("Token refresh response did not include required credentials.");
        }

        var accountId = TryExtractAccountId(tokenResponse.IdToken) ?? session.AccountId;
        var expiresAt = tokenResponse.ExpiresIn.HasValue
            ? DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn.Value)
            : session.ExpiresAt;

        return session with
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken,
            ExpiresAt = expiresAt,
            AccountId = accountId
        };
    }

    private async Task<TokenResponse> ExchangeCodeForTokensAsync(
        string code,
        Uri redirectUri,
        PkceCodes pkce,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(
            $"{ChatGptDefaults.Issuer}/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = redirectUri.ToString(),
                ["client_id"] = ChatGptDefaults.ClientId,
                ["code_verifier"] = pkce.Verifier
            }),
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Token exchange failed ({(int)response.StatusCode}): {body}");
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(payload, JsonOptions)
                            ?? throw new InvalidOperationException("Token exchange response was empty.");
        if (string.IsNullOrWhiteSpace(tokenResponse.AccessToken) || string.IsNullOrWhiteSpace(tokenResponse.RefreshToken))
        {
            throw new InvalidOperationException("Token exchange response did not include required credentials.");
        }

        return tokenResponse;
    }

    private static Uri BuildAuthorizeUrl(Uri redirectUri, PkceCodes pkce, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = ChatGptDefaults.ClientId,
            ["redirect_uri"] = redirectUri.ToString(),
            ["scope"] = "openid profile email offline_access",
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["id_token_add_organizations"] = "true",
            ["codex_cli_simplified_flow"] = "true",
            ["state"] = state,
            ["originator"] = "bendover"
        };

        var query = string.Join("&", parameters.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        return new Uri($"{ChatGptDefaults.Issuer}/oauth/authorize?{query}");
    }

    private static string GenerateState()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string? TryExtractAccountId(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
                .Replace('-', '+')
                .Replace('_', '/');
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            if (doc.RootElement.TryGetProperty("sub", out var sub))
            {
                return sub.GetString();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);

    private sealed record PkceCodes(string Verifier, string Challenge)
    {
        public static PkceCodes Create()
        {
            var verifier = GenerateRandomString(43);
            using var sha256 = SHA256.Create();
            var challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
            var challenge = Base64UrlEncode(challengeBytes);
            return new PkceCodes(verifier, challenge);
        }

        private static string GenerateRandomString(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
            var data = RandomNumberGenerator.GetBytes(length);
            var builder = new StringBuilder(length);
            foreach (var b in data)
            {
                builder.Append(chars[b % chars.Length]);
            }
            return builder.ToString();
        }
    }

    private sealed class OAuthListener : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<string> _codeSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string? _expectedState;

        public OAuthListener()
        {
            var port = GetAvailablePort();
            RedirectUri = new Uri($"http://localhost:{port}/auth/callback");
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
            _ = ListenAsync(_cts.Token);
        }

        public Uri RedirectUri { get; }

        public void SetExpectedState(string expectedState)
        {
            _expectedState = expectedState;
        }

        public async Task<string> WaitForAuthorizationCodeAsync(string expectedState, CancellationToken cancellationToken)
        {
            _expectedState = expectedState;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            linked.Token.Register(() => _codeSource.TrySetCanceled(linked.Token));
            var code = await _codeSource.Task;
            return code;
        }

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                var request = context.Request;
                if (request.Url?.AbsolutePath == "/auth/callback")
                {
                    var query = ParseQuery(request.Url.Query);
                    var error = query.GetValueOrDefault("error");
                    var errorDescription = query.GetValueOrDefault("error_description");
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        _codeSource.TrySetException(new InvalidOperationException(errorDescription ?? error));
                        await WriteHtmlAsync(context.Response, HtmlError(errorDescription ?? error));
                        continue;
                    }

                    var code = query.GetValueOrDefault("code");
                    var state = query.GetValueOrDefault("state");
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        _codeSource.TrySetException(new InvalidOperationException("Missing authorization code."));
                        await WriteHtmlAsync(context.Response, HtmlError("Missing authorization code."));
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(_expectedState) && !string.Equals(state, _expectedState, StringComparison.Ordinal))
                    {
                        _codeSource.TrySetException(new InvalidOperationException("Invalid state returned by authorization server."));
                        await WriteHtmlAsync(context.Response, HtmlError("Invalid state returned by authorization server."));
                        continue;
                    }

                    _codeSource.TrySetResult(code);
                    await WriteHtmlAsync(context.Response, HtmlSuccess());
                    continue;
                }

                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }

        private static async Task WriteHtmlAsync(HttpListenerResponse response, string html)
        {
            response.ContentType = "text/html";
            var buffer = Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer);
            response.Close();
        }

        private static Dictionary<string, string?> ParseQuery(string query)
        {
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(query))
            {
                return result;
            }

            var trimmed = query.TrimStart('?');
            foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                var key = Uri.UnescapeDataString(parts[0]);
                var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
                result[key] = value;
            }

            return result;
        }

        private static string HtmlSuccess()
        {
            return """
                   <!doctype html>
                   <html>
                     <head>
                       <title>Bendover - Authorization Successful</title>
                       <style>
                         body {
                           font-family: system-ui, -apple-system, sans-serif;
                           display: flex;
                           justify-content: center;
                           align-items: center;
                           height: 100vh;
                           margin: 0;
                           background: #0f1115;
                           color: #f0f0f0;
                         }
                         .container { text-align: center; padding: 2rem; }
                         h1 { margin-bottom: 0.5rem; }
                         p { color: #b0b0b0; }
                       </style>
                     </head>
                     <body>
                       <div class="container">
                         <h1>Authorization Successful</h1>
                         <p>You can close this window and return to Bendover.</p>
                       </div>
                       <script>
                         setTimeout(() => window.close(), 2000)
                       </script>
                     </body>
                   </html>
                   """;
        }

        private static string HtmlError(string message)
        {
            return """
                    <!doctype html>
                    <html>
                      <head>
                        <title>Bendover - Authorization Failed</title>
                        <style>
                          body {{
                            font-family: system-ui, -apple-system, sans-serif;
                            display: flex;
                            justify-content: center;
                            align-items: center;
                            height: 100vh;
                            margin: 0;
                            background: #140f0f;
                            color: #f7e1e1;
                          }}
                          .container {{ text-align: center; padding: 2rem; }}
                          h1 {{ margin-bottom: 0.5rem; }}
                          p {{ color: #e6bcbc; }}
                        </style>
                      </head>
                      <body>
                        <div class="container">
                          <h1>Authorization Failed</h1>
                          <p>{WebUtility.HtmlEncode(message)}</p>
                        </div>
                      </body>
                    </html>
                    """;
        }

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Close();
            _cts.Dispose();
        }
    }
}
