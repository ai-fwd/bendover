using Microsoft.Extensions.AI;

namespace Bendover.Application.Turn;

public interface ITranscriptWriter
{
    Task WritePromptAsync(string phase, IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<string> selectedPractices);
    Task WriteOutputAsync(string phase, string output);
    Task WriteFailureAsync(string phase, string failureDigest);
}
