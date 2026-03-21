namespace Mystro.Infrastructure.ChatGpt;

public static class ChatGptDefaults
{
    public const string Issuer = "https://auth.openai.com";
    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public const string Originator = "mystro";
    public const int OAuthPort = 1455;
    public const string DefaultModel = "gpt-5.3-codex";
    public const string CodexResponsesEndpoint = "https://chatgpt.com/backend-api/codex/responses";
    public const string ModelsUrl = "https://chatgpt.com/backend-api/models";
    public const int ChatRequestTimeoutSeconds = 240;
}
