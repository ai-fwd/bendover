using System.Text.Json;
using Bendover.Domain.Interfaces;

namespace Bendover.Application.Turn;

public sealed class FinalizeTurnStep : TurnStep
{
    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        var observation = context.Observation
            ?? throw new InvalidOperationException("Turn observation was not produced.");

        context.SerializedObservation = JsonSerializer.Serialize(observation);

        if (context.Run.RunRecording.RecordOutput)
        {
            await context.Run.RunRecorder.RecordOutputAsync(context.ObservationPhase, context.SerializedObservation);
        }

        await context.Run.TranscriptWriter.WriteOutputAsync(context.ObservationPhase, context.SerializedObservation);
        await context.Run.NotifyStepAsync(new AgentStepEvent(
            StepNumber: context.StepNumber,
            Plan: observation.StepPlan,
            Tool: TurnContent.BuildStepTool(observation),
            Observation: TurnContent.BuildStepObservationSummary(observation),
            IsCompletion: observation.CompletionSignaled));

        if (observation.ScriptExecution.ExitCode != 0)
        {
            var failureDigest = TurnContent.BuildTurnFailureDigest(observation, new[] { "script_exit_non_zero" });
            context.RunState.LastFailureDigest = failureDigest;
            context.Result = TurnResult.FailedRetryable(failureDigest);

            if (context.Run.RunRecording.RecordOutput)
            {
                await context.Run.RunRecorder.RecordOutputAsync(context.FailurePhase, failureDigest);
            }

            await context.Run.TranscriptWriter.WriteFailureAsync(context.FailurePhase, failureDigest);
            return;
        }

        context.RunState.LastFailureDigest = null;
        context.Result = observation.CompletionSignaled
            ? TurnResult.Completed
            : TurnResult.Continue;

        await next(context);
    }
}
