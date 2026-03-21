using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Turn;

public sealed class RunContext
{
    public required TurnStepFactory StepFactory { get; init; }
    public required ITranscriptWriter TranscriptWriter { get; init; }
    public required IPromptOptRunRecorder RunRecorder { get; init; }
    public required IChatClient EngineerClient { get; init; }
    public required IAgenticTurnService AgenticTurnService { get; init; }
    public required IAgentEventPublisher Events { get; init; }
    public required string EngineerPromptTemplate { get; init; }
    public required IReadOnlyCollection<string> SelectedPractices { get; init; }
}
