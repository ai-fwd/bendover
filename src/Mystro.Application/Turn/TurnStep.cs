namespace Mystro.Application.Turn;

public abstract class TurnStep
{
    public abstract Task InvokeAsync(TurnContext context, TurnDelegate next);
}
