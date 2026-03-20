using Bendover.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Turn;

public sealed class InvokeAgentStep : TurnStep
{
    private readonly TurnCapabilities _capabilities;
    private readonly IPromptOptRunRecorder _runRecorder;
    private readonly IChatClient _engineerClient;

    public InvokeAgentStep(TurnCapabilities capabilities, IPromptOptRunRecorder runRecorder, IChatClient engineerClient)
    {
        _capabilities = capabilities;
        _runRecorder = runRecorder;
        _engineerClient = engineerClient;
    }

    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        if (_capabilities.RunRecording.RecordPrompt)
        {
            await _runRecorder.RecordPromptAsync(context.EngineerPhase, context.EngineerMessages);
        }

        await _capabilities.TranscriptWriter.WritePromptAsync(
            context.EngineerPhase,
            context.EngineerMessages,
            context.SelectedPractices);

        var actorCodeResponse = await _engineerClient.CompleteAsync(context.EngineerMessages);
        context.ActorCode = actorCodeResponse.Message.Text ?? string.Empty;

        if (_capabilities.RunRecording.RecordOutput)
        {
            await _runRecorder.RecordOutputAsync(context.EngineerPhase, context.ActorCode);
        }

        await _capabilities.TranscriptWriter.WriteOutputAsync(context.EngineerPhase, context.ActorCode);
        await next(context);
    }
}
