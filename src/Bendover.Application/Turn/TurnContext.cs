using Bendover.Domain.Entities;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Turn;

public sealed class TurnContext
{
    public required int StepNumber { get; init; }
    public required string EngineerPromptTemplate { get; init; }
    public required string PracticesContext { get; init; }
    public required string Plan { get; init; }
    public required IReadOnlyCollection<string> SelectedPracticeNames { get; init; }
    public required List<TurnHistoryEntry> StepHistory { get; init; }
    public required AgenticTurnSettings TurnSettings { get; init; }

    public string EngineerPhase => $"engineer_step_{StepNumber}";
    public string ObservationPhase => $"agentic_step_observation_{StepNumber}";
    public string FailurePhase => $"engineer_step_failure_{StepNumber}";

    public string ContextBlock { get; set; } = string.Empty;
    public List<ChatMessage> EngineerMessages { get; set; } = new();
    public string ActorCode { get; set; } = string.Empty;
    public AgenticTurnObservation? Observation { get; set; }
    public string SerializedObservation { get; set; } = string.Empty;
    public string? FailureDigest { get; set; }
    public bool StepFailed { get; set; }
    public bool CompletionSignaled { get; set; }
    public int? LastScriptExitCode { get; set; }
    public Exception? UnhandledException { get; set; }
}
