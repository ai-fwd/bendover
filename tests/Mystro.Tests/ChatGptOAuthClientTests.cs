using System.Net;
using System.Text;
using Mystro.Infrastructure.ChatGpt;

namespace Mystro.Tests;

public sealed class ChatGptOAuthClientTests
{
    [Fact]
    public async Task RefreshAsync_ShouldExtractChatGptAccountId_FromIdToken()
    {
        var idToken = BuildUnsignedJwt("""{"chatgpt_account_id":"account-from-id-token"}""");
        var responseBody = $$"""
            {
              "access_token":"{{BuildUnsignedJwt("""{"sub":"user"}""")}}",
              "refresh_token":"new-refresh-token",
              "id_token":"{{idToken}}",
              "expires_in":3600
            }
            """;

        var client = CreateClient(responseBody);
        var session = new ChatGptAuthSession(
            AccessToken: "old-access-token",
            RefreshToken: "old-refresh-token",
            ExpiresAt: DateTimeOffset.UtcNow,
            AccountId: "existing-account",
            Email: null);

        var refreshed = await client.RefreshAsync(session, CancellationToken.None);

        Assert.Equal("account-from-id-token", refreshed.AccountId);
    }

    [Fact]
    public async Task RefreshAsync_ShouldFallbackToAccessTokenClaims_WhenIdTokenHasNoAccountId()
    {
        var idToken = BuildUnsignedJwt("""{"sub":"user"}""");
        var accessToken = BuildUnsignedJwt("""{"organizations":[{"id":"org-from-access-token"}]}""");
        var responseBody = $$"""
            {
              "access_token":"{{accessToken}}",
              "refresh_token":"new-refresh-token",
              "id_token":"{{idToken}}",
              "expires_in":3600
            }
            """;

        var client = CreateClient(responseBody);
        var session = new ChatGptAuthSession(
            AccessToken: "old-access-token",
            RefreshToken: "old-refresh-token",
            ExpiresAt: DateTimeOffset.UtcNow,
            AccountId: "existing-account",
            Email: null);

        var refreshed = await client.RefreshAsync(session, CancellationToken.None);

        Assert.Equal("org-from-access-token", refreshed.AccountId);
    }

    [Fact]
    public async Task RefreshAsync_ShouldPreserveExistingAccountId_WhenTokensContainNoAccountIdClaims()
    {
        var responseBody = $$"""
            {
              "access_token":"{{BuildUnsignedJwt("""{"sub":"user"}""")}}",
              "refresh_token":"new-refresh-token",
              "id_token":"{{BuildUnsignedJwt("""{"email":"user@example.com"}""")}}",
              "expires_in":3600
            }
            """;

        var client = CreateClient(responseBody);
        var session = new ChatGptAuthSession(
            AccessToken: "old-access-token",
            RefreshToken: "old-refresh-token",
            ExpiresAt: DateTimeOffset.UtcNow,
            AccountId: "existing-account",
            Email: null);

        var refreshed = await client.RefreshAsync(session, CancellationToken.None);

        Assert.Equal("existing-account", refreshed.AccountId);
    }

    private static ChatGptOAuthClient CreateClient(string responseBody)
    {
        var handler = new StaticResponseHttpMessageHandler(
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        return new ChatGptOAuthClient(new HttpClient(handler));
    }

    private static string BuildUnsignedJwt(string jsonPayload)
    {
        var header = Base64UrlEncode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncode(jsonPayload);
        return $"{header}.{payload}.signature";
    }

    private static string Base64UrlEncode(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class StaticResponseHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responseFactory(request));
        }
    }
}
