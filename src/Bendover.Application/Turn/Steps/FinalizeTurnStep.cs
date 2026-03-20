using System.Text.Json;
using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;

namespace Bendover.Application.Turn;

public sealed class FinalizeTurnStep : TurnStep
{
    private readonly TurnCapabilities _capabilities;
    private readonly IPromptOptRunRecorder _runRecorder;
    private readonly Func<AgentStepEvent, Task> _notifyStepAsync;

    public FinalizeTurnStep(
        TurnCapabilities capabilities,
        IPromptOptRunRecorder runRecorder,
        Func<AgentStepEvent, Task> notifyStepAsync)
    {
        _capabilities = capabilities;
        _runRecorder = runRecorder;
        _notifyStepAsync = notifyStepAsync;
    }

    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        var observation = context.Observation
            ?? throw new InvalidOperationException("Turn observation was not produced.");

        context.SerializedObservation = JsonSerializer.Serialize(observation);

        if (_capabilities.RunRecording.RecordOutput)
        {
            await _runRecorder.RecordOutputAsync(context.ObservationPhase, context.SerializedObservation);
        }

        await _capabilities.TranscriptWriter.WriteOutputAsync(context.ObservationPhase, context.SerializedObservation);
        await _notifyStepAsync(new AgentStepEvent(
            StepNumber: context.StepNumber,
            Plan: observation.StepPlan,
            Tool: TurnContent.BuildStepTool(observation),
            Observation: TurnContent.BuildStepObservationSummary(observation),
            IsCompletion: observation.CompletionSignaled));

        if (observation.ScriptExecution.ExitCode != 0)
        {
            var failureDigest = TurnContent.BuildTurnFailureDigest(observation, new[] { "script_exit_non_zero" });
            context.Run.LastFailureDigest = failureDigest;
            context.Result = TurnResult.FailedRetryable(failureDigest);

            if (_capabilities.RunRecording.RecordOutput)
            {
                await _runRecorder.RecordOutputAsync(context.FailurePhase, failureDigest);
            }

            await _capabilities.TranscriptWriter.WriteFailureAsync(context.FailurePhase, failureDigest);
            return;
        }

        context.Run.LastFailureDigest = null;
        context.Result = observation.CompletionSignaled
            ? TurnResult.Completed
            : TurnResult.Continue;

        await next(context);
    }
}
