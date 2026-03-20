namespace Bendover.Application.Turn;

public sealed class BuildPromptStep : TurnStep
{
    public override Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.EngineerMessages = TurnContent.BuildEngineerMessages(
            context.Run.EngineerPromptTemplate,
            context.PracticesContext,
            context.Plan,
            context.ContextBlock);

        return next(context);
    }
}
