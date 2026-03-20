using Microsoft.Extensions.AI;
using Bendover.Application.Interfaces;

namespace Bendover.Application.Turn;

public sealed class NoOpTranscriptWriter : ITranscriptWriter
{
    public Task WritePromptAsync(string phase, IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<string> selectedPractices)
        => Task.CompletedTask;

    public Task WriteOutputAsync(string phase, string output)
        => Task.CompletedTask;

    public Task WriteFailureAsync(string phase, string failureDigest)
        => Task.CompletedTask;
}
