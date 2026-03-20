namespace Bendover.Application.Turn;

public sealed class BuildPromptStep : TurnStep
{
    private readonly string _engineerPromptTemplate;

    public BuildPromptStep(string engineerPromptTemplate)
    {
        _engineerPromptTemplate = engineerPromptTemplate;
    }

    public override Task InvokeAsync(TurnContext context, TurnDelegate next)
    {
        context.EngineerMessages = TurnContent.BuildEngineerMessages(
            _engineerPromptTemplate,
            context.PracticesContext,
            context.Plan,
            context.ContextBlock);

        return next(context);
    }
}
