namespace Bendover.Infrastructure.ChatGpt;

public sealed class ChatGptTokenProvider
{
    private readonly ChatGptAuthStore _store;
    private readonly ChatGptOAuthClient _oauthClient;

    public ChatGptTokenProvider(ChatGptAuthStore store, ChatGptOAuthClient oauthClient)
    {
        _store = store;
        _oauthClient = oauthClient;
    }

    public ChatGptAuthSession? LoadSession()
    {
        return _store.Load();
    }

    public async Task<ChatGptAuthSession?> GetValidSessionAsync(CancellationToken cancellationToken)
    {
        var session = _store.Load();
        if (session == null)
        {
            return null;
        }

        if (session.ExpiresAt.HasValue && session.ExpiresAt.Value > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return session;
        }

        if (string.IsNullOrWhiteSpace(session.RefreshToken))
        {
            return session;
        }

        var refreshed = await _oauthClient.RefreshAsync(session, cancellationToken);
        _store.Save(refreshed);
        return refreshed;
    }
}
