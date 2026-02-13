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
        if (args.Any(arg => string.Equals(arg, "/connect", StringComparison.OrdinalIgnoreCase)))
        {
            var connector = new ChatGptConnector();
            await connector.RunAsync();
            return;
        }

        if (args.Any(arg => string.Equals(arg, "/disconnect", StringComparison.OrdinalIgnoreCase)))
        {
            var disconnector = new ChatGptDisconnector();
            disconnector.Run();
            return;
        }

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
