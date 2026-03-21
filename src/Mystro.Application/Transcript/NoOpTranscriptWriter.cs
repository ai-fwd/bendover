using Mystro.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace Mystro.Application.Transcript;

public sealed class NoOpTranscriptWriter : ITranscriptWriter
{
    public Task WriteSelectedPracticesAsync(IReadOnlyCollection<string> selectedPractices)
        => Task.CompletedTask;

    public Task WritePromptAsync(string phase, IReadOnlyList<ChatMessage> messages)
        => Task.CompletedTask;

    public Task WriteOutputAsync(string phase, string output)
        => Task.CompletedTask;

    public Task WriteFailureAsync(string phase, string failureDigest)
        => Task.CompletedTask;
}
