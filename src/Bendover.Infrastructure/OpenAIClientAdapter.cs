using Bendover.Domain.Interfaces;

namespace Bendover.Infrastructure;

public class OpenAIClientAdapter : IChatClient
{
    public Task<string> CompleteAsync(string systemPrompt, string userPrompt)
    {
        // Stub implementation
        return Task.FromResult($"[STUB RESPONSE] to: {userPrompt}");
    }
}
