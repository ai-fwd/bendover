namespace Bendover.Application.Turn;

public sealed class GuardTurnStep : TurnStep
{
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

            if (context.Run.RunRecording.RecordOutput)
            {
                await context.Run.RunRecorder.RecordOutputAsync(context.FailurePhase, failureDigest);
            }

            await context.Run.TranscriptWriter.WriteFailureAsync(context.FailurePhase, failureDigest);
        }
    }
}
