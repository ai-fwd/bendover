namespace Bendover.Application.Turn;

public sealed class BuildContextStep : TurnStep
{
    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.ContextBlock = TurnContent.BuildContextBlock(context.Run.StepHistory);
        await next(context);

        if (context.Result.Kind == TurnResultKind.FailedTerminal && context.Result.Exception is not null)
        {
            TurnContent.AppendStepHistory(
                context.Run.StepHistory,
                context.StepNumber,
                TurnContent.BuildExceptionObservationContext(context.Result.Exception),
                context.Result.FailureDigest);
            return;
        }

        if (context.Result.Kind == TurnResultKind.FailedRetryable)
        {
            TurnContent.AppendStepHistory(
                context.Run.StepHistory,
                context.StepNumber,
                context.SerializedObservation,
                context.Result.FailureDigest);
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.SerializedObservation))
        {
            TurnContent.AppendStepHistory(
                context.Run.StepHistory,
                context.StepNumber,
                context.SerializedObservation,
                failureContext: null);
        }
    }
}
