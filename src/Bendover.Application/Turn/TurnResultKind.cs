namespace Bendover.Application.Turn;

public enum TurnResultKind
{
    Continue,
    FailedRetryable,
    Completed,
    FailedTerminal
}
