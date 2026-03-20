using Bendover.Application.Interfaces;

namespace Bendover.Application.Turn;

public sealed class ExecuteTurnStep : TurnStep
{
    private readonly IAgenticTurnService _agenticTurnService;

    public ExecuteTurnStep(IAgenticTurnService agenticTurnService)
    {
        _agenticTurnService = agenticTurnService;
    }

    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.Observation = await _agenticTurnService.ExecuteAgenticTurnAsync(context.ActorCode, context.Run.TurnSettings);
        context.Run.LastScriptExitCode = context.Observation.ScriptExecution.ExitCode;
        await next(context);
    }
}
