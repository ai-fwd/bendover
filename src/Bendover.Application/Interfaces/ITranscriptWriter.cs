using Microsoft.Extensions.AI;

namespace Bendover.Application.Interfaces;

public interface ITranscriptWriter
{
    Task WriteSelectedPracticesAsync(IReadOnlyCollection<string> selectedPractices);
    Task WritePromptAsync(string phase, IReadOnlyList<ChatMessage> messages);
    Task WriteOutputAsync(string phase, string output);
    Task WriteFailureAsync(string phase, string failureDigest);
}
