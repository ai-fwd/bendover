using System.Net.Http.Headers;

namespace Bendover.Infrastructure.ChatGpt;

public sealed class ChatGptSessionVerifier
{
    private readonly HttpClient _httpClient;

    public ChatGptSessionVerifier(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task VerifyAsync(ChatGptAuthSession session, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ChatGptDefaults.ModelsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Headers.UserAgent.ParseAdd("BendoverCLI/1.0");
        request.Headers.TryAddWithoutValidation("originator", ChatGptDefaults.Originator);
        if (!string.IsNullOrWhiteSpace(session.AccountId))
        {
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", session.AccountId);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Verification failed ({(int)response.StatusCode}): {body}");
        }
    }
}
