namespace Bendover.Application.Turn;

public sealed class GuardTurnStep : TurnStep
{
    private readonly RunContext _run;

    public GuardTurnStep(RunContext run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var failureDigest = TurnContent.BuildFailureDigest(
                exitCode: 1,
                exception: ex,
                combinedOutput: ex.ToString());
            context.RunState.LastFailureDigest = failureDigest;
            context.Result = TurnResult.FailedTerminal(failureDigest, ex);
            await _run.RunRecorder.RecordOutputAsync(context.FailurePhase, failureDigest);

            await _run.TranscriptWriter.WriteFailureAsync(context.FailurePhase, failureDigest);
        }
    }
}
