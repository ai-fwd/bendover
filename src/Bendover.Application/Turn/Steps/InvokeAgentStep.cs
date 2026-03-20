namespace Bendover.Application.Turn;

public sealed class InvokeAgentStep : TurnStep
{
    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        if (context.Run.RunRecording.RecordPrompt)
        {
            await context.Run.RunRecorder.RecordPromptAsync(context.EngineerPhase, context.EngineerMessages);
        }

        await context.Run.TranscriptWriter.WritePromptAsync(context.EngineerPhase, context.EngineerMessages);

        var actorCodeResponse = await context.Run.EngineerClient.CompleteAsync(context.EngineerMessages);
        context.ActorCode = actorCodeResponse.Message.Text ?? string.Empty;

        if (context.Run.RunRecording.RecordOutput)
        {
            await context.Run.RunRecorder.RecordOutputAsync(context.EngineerPhase, context.ActorCode);
        }

        await context.Run.TranscriptWriter.WriteOutputAsync(context.EngineerPhase, context.ActorCode);
        await next(context);
    }
}
