using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Application;
using Bendover.Domain.Exceptions;
using Microsoft.Extensions.Configuration;
using Bendover.Infrastructure.Configuration;
using Bendover.Domain;

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
        services.AddSingleton<GovernanceEngine>();
        services.AddSingleton<ScriptGenerator>();
        services.AddSingleton<IAgentObserver, ConsoleAgentObserver>();
        services.AddSingleton<ILeadAgent, FakeLeadAgent>();
        services.AddSingleton<IPracticeService, PracticeService>();
        
        // Logging (Quiet for now)
        services.AddLogging(configure => configure.AddConsole().SetMinimumLevel(LogLevel.Warning));

        var serviceProvider = services.BuildServiceProvider();

        try
        {
            AnsiConsole.MarkupLine("[bold blue]Starting Bendover Agent (Local)...[/]");
            
            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            
            // In a real scenario, argument handling would go here.
            await orchestrator.RunAsync("Self-Improvement");
            
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
