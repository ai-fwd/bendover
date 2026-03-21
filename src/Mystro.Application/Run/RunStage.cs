namespace Mystro.Application.Run;

public abstract class RunStage
{
    public virtual int SetupOrder => 0;

    public virtual int TeardownOrder => 0;

    public virtual Task SetupAsync(RunStageContext context)
    {
        return Task.CompletedTask;
    }

    public virtual Task ExecuteAsync(RunStageContext context)
    {
        return Task.CompletedTask;
    }

    public virtual Task TeardownAsync(RunStageContext context)
    {
        return Task.CompletedTask;
    }
}
