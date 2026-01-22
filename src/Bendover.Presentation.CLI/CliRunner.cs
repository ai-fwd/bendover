namespace Bendover.Presentation.CLI;

public enum ExecutionMode
{
    Local,
    Remote
}

public class CliRunner
{
    public async Task RunAsync(string[] args)
    {
        IAgentRunner runner;
        var mode = GetExecutionMode(args);

        if (mode == ExecutionMode.Remote)
        {
            runner = new RemoteAgentRunner();
        }
        else
        {
            runner = new LocalAgentRunner();
        }

        await runner.RunAsync(args);
    }

    public ExecutionMode GetExecutionMode(string[] args)
    {
        if (args.Contains("--remote"))
        {
            return ExecutionMode.Remote;
        }
        return ExecutionMode.Local;
    }
}
