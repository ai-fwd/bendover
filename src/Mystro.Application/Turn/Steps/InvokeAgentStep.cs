namespace Mystro.Application.Turn;

public sealed class InvokeAgentStep : TurnStep
{
    private readonly RunContext _run;

    public InvokeAgentStep(RunContext run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        await _run.RunRecorder.RecordPromptAsync(context.EngineerPhase, context.EngineerMessages);

        await _run.TranscriptWriter.WritePromptAsync(context.EngineerPhase, context.EngineerMessages);

        var actorCodeResponse = await _run.EngineerClient.CompleteAsync(context.EngineerMessages);
        context.ActorCode = actorCodeResponse.Message.Text ?? string.Empty;

        await _run.RunRecorder.RecordOutputAsync(context.EngineerPhase, context.ActorCode);

        await _run.TranscriptWriter.WriteOutputAsync(context.EngineerPhase, context.ActorCode);
        await next(context);
    }
}
