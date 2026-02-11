namespace Bendover.Infrastructure.ChatGpt;

public sealed record ChatGptAuthSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? AccountId,
    string? Email
);
