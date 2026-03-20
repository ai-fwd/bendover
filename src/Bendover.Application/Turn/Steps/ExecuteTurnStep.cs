namespace Bendover.Application.Turn;

public sealed class ExecuteTurnStep : TurnStep
{
    private readonly RunContext _run;

    public ExecuteTurnStep(RunContext run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.Observation = await _run.AgenticTurnService.ExecuteAgenticTurnAsync(context.ActorCode);
        context.RunState.LastScriptExitCode = context.Observation.ScriptExecution.ExitCode;
        await next(context);
    }
}
