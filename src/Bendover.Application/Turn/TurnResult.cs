namespace Bendover.Application.Turn;

public sealed record TurnResult(
    TurnResultKind Kind,
    string? FailureDigest = null,
    Exception? Exception = null)
{
    public static TurnResult Continue { get; } = new(TurnResultKind.Continue);

    public static TurnResult FailedRetryable(string failureDigest)
    {
        return new TurnResult(TurnResultKind.FailedRetryable, failureDigest);
    }

    public static TurnResult Completed { get; } = new(TurnResultKind.Completed);

    public static TurnResult FailedTerminal(string failureDigest, Exception exception)
    {
        return new TurnResult(TurnResultKind.FailedTerminal, failureDigest, exception);
    }
}
