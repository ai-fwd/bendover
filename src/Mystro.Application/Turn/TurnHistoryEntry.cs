namespace Mystro.Application.Turn;

public sealed record TurnHistoryEntry(
    int StepNumber,
    string ObservationContext,
    string? FailureContext);
