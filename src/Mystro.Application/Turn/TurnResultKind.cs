namespace Mystro.Application.Turn;

public enum TurnResultKind
{
    Continue,
    FailedRetryable,
    Completed,
    FailedTerminal
}
