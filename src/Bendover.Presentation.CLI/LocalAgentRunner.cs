using Bendover.Application;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Exceptions;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Configuration;
using Bendover.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Bendover.Presentation.CLI;

public class LocalAgentRunner : IAgentRunner
{
    public async Task RunAsync(string[] args)
    {
        // Setup Dependency Injection
        var services = new ServiceCollection();

        // Build Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .Build();

        // Application & Infrastructure
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();
        services.AddSingleton<IEnvironmentValidator, DockerEnvironmentValidator>();
        services.AddSingleton<IContainerService, DockerContainerService>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<ScriptGenerator>();
        services.AddSingleton<IAgentObserver, ConsoleAgentObserver>();
        services.AddSingleton<System.IO.Abstractions.IFileSystem, System.IO.Abstractions.FileSystem>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<ILeadAgent, LeadAgent>();
        services.AddSingleton<IPracticeService, PracticeService>();

        // New Services for Prompt Opt
        services.AddSingleton<IGitRunner, GitRunner>();
        services.AddSingleton<IDotNetRunner, DotNetRunner>();
        services.AddSingleton<IPromptBundleResolver>(_ => new PromptBundleResolver(Directory.GetCurrentDirectory()));
        services.AddSingleton<EvaluatorEngine>();
        services.AddSingleton<IEnumerable<IEvaluatorRule>>(Enumerable.Empty<IEvaluatorRule>());
        services.AddSingleton<IPromptOptRunContextAccessor, PromptOptRunContextAccessor>();
        services.AddSingleton<IPromptOptRunRecorder, PromptOptRunRecorder>();

        // Logging (Quiet for now)
        services.AddLogging(configure => configure.AddConsole().SetMinimumLevel(LogLevel.Warning));

        var serviceProvider = services.BuildServiceProvider();

        try
        {
            AnsiConsole.MarkupLine("[bold blue]Starting Bendover Agent (Local)...[/]");

            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            var runContextAccessor = serviceProvider.GetRequiredService<IPromptOptRunContextAccessor>();

            // Interactive Mode
            var goal = AnsiConsole.Ask<string>("[bold yellow]What do you want to build?[/]");

            var runId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
            var evaluate = configuration.GetValue("PromptOpt:Evaluate", false);
            var outDir = Path.Combine(".bendover", "promptopt", "runs", runId);
            runContextAccessor.Current = new PromptOptRunContext(
                outDir,
                Capture: true,
                Evaluate: evaluate,
                RunId: runId
            );

            AnsiConsole.MarkupLine("[bold purple]🎵take it easy, I will do the work...🎵[/]");

            await orchestrator.RunAsync(goal);

            AnsiConsole.MarkupLine("[bold green]Agent Finished Successfully.[/]");
        }
        catch (DockerUnavailableException ex)
        {
            var panel = new Panel($"[bold red]Docker Error[/]\n\n{ex.Message}")
               .BorderColor(Color.Red)
               .Header("Environment Failure");
            AnsiConsole.Write(panel);
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            Environment.Exit(1);
        }
    }
}
