using System.Text;

namespace Bendover.Application.Turn;

public sealed class BuildContextStep : TurnStep
{
    private const int PromptHistoryDepth = 5;

    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.ContextBlock = BuildContextBlock(context.RunState.StepHistory);
        await next(context);

        if (context.Result.Kind == TurnResultKind.FailedTerminal && context.Result.Exception is not null)
        {
            AppendStepHistory(
                context.RunState.StepHistory,
                context.StepNumber,
                TurnContent.BuildExceptionObservationContext(context.Result.Exception),
                context.Result.FailureDigest);
            return;
        }

        if (context.Result.Kind == TurnResultKind.FailedRetryable)
        {
            AppendStepHistory(
                context.RunState.StepHistory,
                context.StepNumber,
                context.SerializedObservation,
                context.Result.FailureDigest);
            return;
        }

        if (!string.IsNullOrWhiteSpace(context.SerializedObservation))
        {
            AppendStepHistory(
                context.RunState.StepHistory,
                context.StepNumber,
                context.SerializedObservation,
                failureContext: null);
        }
    }

    private static string BuildContextBlock(IReadOnlyList<TurnHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return string.Empty;
        }

        var historyBuilder = new StringBuilder();
        historyBuilder.AppendLine($"Recent step history (oldest to newest, last {PromptHistoryDepth}):");
        foreach (var entry in history)
        {
            historyBuilder.AppendLine($"Step {entry.StepNumber} observation (raw):");
            historyBuilder.AppendLine(entry.ObservationContext);
            if (!string.IsNullOrWhiteSpace(entry.FailureContext))
            {
                historyBuilder.AppendLine($"Step {entry.StepNumber} failure (raw):");
                historyBuilder.AppendLine(entry.FailureContext);
            }

            historyBuilder.AppendLine();
        }

        return historyBuilder.ToString().TrimEnd();
    }

    private static void AppendStepHistory(
        List<TurnHistoryEntry> stepHistory,
        int stepNumber,
        string observationContext,
        string? failureContext)
    {
        stepHistory.Add(new TurnHistoryEntry(stepNumber, observationContext, failureContext));
        var overflow = stepHistory.Count - PromptHistoryDepth;
        if (overflow > 0)
        {
            stepHistory.RemoveRange(0, overflow);
        }
    }
}
