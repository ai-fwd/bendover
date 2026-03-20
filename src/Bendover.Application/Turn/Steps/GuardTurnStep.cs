using Bendover.Application.Interfaces;

namespace Bendover.Application.Turn;

public sealed class GuardTurnStep : TurnStep
{
    private readonly TurnCapabilities _capabilities;
    private readonly IPromptOptRunRecorder _runRecorder;

    public GuardTurnStep(TurnCapabilities capabilities, IPromptOptRunRecorder runRecorder)
    {
        _capabilities = capabilities;
        _runRecorder = runRecorder;
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
            context.Run.LastFailureDigest = failureDigest;
            context.Result = TurnResult.FailedTerminal(failureDigest, ex);

            if (_capabilities.RunRecording.RecordOutput)
            {
                await _runRecorder.RecordOutputAsync(context.FailurePhase, failureDigest);
            }

            await _capabilities.TranscriptWriter.WriteFailureAsync(context.FailurePhase, failureDigest);
        }
    }
}
