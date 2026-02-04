using System.IO.Abstractions;
using System.Threading.Tasks;
using System.Threading;
using Bendover.Application;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Services;
using Spectre.Console.Cli;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Bendover.Infrastructure.Configuration;

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
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();
        services.AddSingleton<IEnvironmentValidator, DockerEnvironmentValidator>();
        services.AddSingleton<IContainerService, DockerContainerService>();
        services.AddSingleton<IAgentOrchestratorFactory, PromptOptAgentOrchestratorFactory>();
        services.AddSingleton<ScriptGenerator>();
        services.AddSingleton<IAgentObserver, NoOpAgentObserver>();
        services.AddSingleton<System.IO.Abstractions.IFileSystem, System.IO.Abstractions.FileSystem>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<ILeadAgent, LeadAgent>();
        services.AddSingleton<IPracticeService, PracticeService>();
        services.AddSingleton<IGitRunner, GitRunner>();
        services.AddSingleton<IDotNetRunner, DotNetRunner>();
        services.AddSingleton<IPromptBundleResolver>(_ => new PromptBundleResolver(Directory.GetCurrentDirectory()));
        services.AddSingleton<EvaluatorEngine>();
        services.AddSingleton<IEnumerable<IEvaluatorRule>>(Enumerable.Empty<IEvaluatorRule>());
        services.AddSingleton<IPromptOptRunContextAccessor, PromptOptRunContextAccessor>();
        services.AddSingleton<IPromptOptRunRecorder, PromptOptRunRecorder>();
        services.AddSingleton<IPromptOptRunEvaluator, PromptOptRunEvaluator>();

        var serviceProvider = services.BuildServiceProvider();

        var fileSystem = serviceProvider.GetRequiredService<System.IO.Abstractions.IFileSystem>();
        var gitRunner = serviceProvider.GetRequiredService<IGitRunner>();
        var resolver = serviceProvider.GetRequiredService<IPromptBundleResolver>();
        var agentOrchestratorFactory = serviceProvider.GetRequiredService<IAgentOrchestratorFactory>();
        var runContextAccessor = serviceProvider.GetRequiredService<IPromptOptRunContextAccessor>();
        var runEvaluator = serviceProvider.GetRequiredService<IPromptOptRunEvaluator>();

        var orchestrator = new BenchmarkRunOrchestrator(
            gitRunner,
            agentOrchestratorFactory,
            resolver,
            runEvaluator,
            fileSystem,
            runContextAccessor
        );

        if (string.IsNullOrWhiteSpace(settings.Bundle) || string.IsNullOrWhiteSpace(settings.Task))
        {
            throw new InvalidOperationException("Bundle and Task are required.");
        }

        await orchestrator.RunAsync(
            settings.Bundle,
            settings.Task,
            settings.Out
        );
        return 0;
    }
}

public class RunCommandSettings : CommandSettings
{
    [CommandOption("--bundle <PATH>")]
    [Description("Path to the bundle directory")]
    public string? Bundle { get; init; }

    [CommandOption("--task <PATH>")]
    [Description("Path to the task directory")]
    public string? Task { get; init; }

    [CommandOption("--out <PATH>")]
    [Description("Path to the output directory")]
    public required string Out { get; init; }
}
