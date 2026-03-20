using Bendover.Domain.Entities;

namespace Bendover.Application.Turn;

public sealed class TurnRunState
{
    public required List<TurnHistoryEntry> StepHistory { get; init; }
    public required AgenticTurnSettings TurnSettings { get; init; }

    public int? LastScriptExitCode { get; set; }
    public string? LastFailureDigest { get; set; }
}
