using System.IO.Abstractions;
using System.Threading.Tasks;
using System.Threading;
using Bendover.Application;
using Spectre.Console.Cli;
using System.ComponentModel;

namespace Bendover.PromptOpt.CLI;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        var app = new CommandApp<RunCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("bendover-promptopt");
        });
        return app.RunAsync(args);
    }
}

public class RunCommand : AsyncCommand<RunCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RunCommandSettings settings, CancellationToken cancellationToken)
    {
        var fileSystem = new FileSystem();
        var gitRunner = new GitRunner();
        var agentRunner = new StubAgentRunner();
        var dotNetRunner = new DotNetRunner();
        
        var repoRoot = System.IO.Directory.GetCurrentDirectory(); 
        var resolver = new PromptBundleResolver(repoRoot);

        var orchestrator = new BenchmarkRunOrchestrator(
            gitRunner,
            agentRunner,
            resolver,
            dotNetRunner,
            fileSystem
        );

        await orchestrator.RunAsync(settings.Bundle, settings.Task, settings.Out);
        return 0;
    }
}

public class RunCommandSettings : CommandSettings
{
    [CommandOption("--bundle <PATH>")]
    [Description("Path to the bundle directory")]
    public required string Bundle { get; init; }

    [CommandOption("--task <PATH>")]
    [Description("Path to the task directory")]
    public required string Task { get; init; }

    [CommandOption("--out <PATH>")]
    [Description("Path to the output directory")]
    public required string Out { get; init; }
}
