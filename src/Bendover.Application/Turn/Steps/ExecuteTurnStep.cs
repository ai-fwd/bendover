namespace Bendover.Application.Turn;

public sealed class ExecuteTurnStep : TurnStep
{
    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.Observation = await context.Run.AgenticTurnService.ExecuteAgenticTurnAsync(context.ActorCode);
        context.RunState.LastScriptExitCode = context.Observation.ScriptExecution.ExitCode;
        await next(context);
    }
}
