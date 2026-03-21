namespace Mystro.Application.Turn;

public sealed class BuildPromptStep : TurnStep
{
    private readonly RunContext _run;

    public BuildPromptStep(RunContext run)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
    }

    public override Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.EngineerMessages = TurnContent.BuildEngineerMessages(
            _run.EngineerPromptTemplate,
            context.PracticesContext,
            context.Plan,
            context.ContextBlock);

        return next(context);
    }
}
