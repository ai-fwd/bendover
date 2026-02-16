using Bendover.Application;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Exceptions;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Configuration;
using Bendover.Infrastructure.Services;
using Bendover.Infrastructure.ChatGpt;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Bendover.Presentation.CLI;

public class LocalAgentRunner : IAgentRunner
{
    private static readonly string[] ControlFlags =
    {
        "--remote",
        "--transcript"
    };

    public async Task RunAsync(string[] args)
    {
        // Setup Dependency Injection
        var services = new ServiceCollection();

        // Build Configuration
        Env.TraversePath().Load(); // Load .env file traversing up the tree

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddEnvironmentVariables()
            .Build();
        var agentOptions = configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
        var lmSummary = BuildLmSummary(agentOptions);

        // Application & Infrastructure
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();
        services.AddSingleton<IAgentPromptService, AgentPromptService>();
        services.AddSingleton<IEnvironmentValidator, DockerEnvironmentValidator>();
        services.AddSingleton<IContainerService, DockerContainerService>();
        services.AddSingleton<IAgenticTurnService, AgenticTurnService>();
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
        services.AddSingleton<IPromptOptRunEvaluator, PromptOptRunEvaluator>();

        // Logging (Quiet for now)
        services.AddLogging(configure => configure.AddConsole().SetMinimumLevel(LogLevel.Warning));

        var serviceProvider = services.BuildServiceProvider();

        try
        {
            AnsiConsole.MarkupLine($"[bold blue]Starting Bendover Agent - {Markup.Escape(lmSummary)}[/]");

            var orchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
            var practiceService = serviceProvider.GetRequiredService<IPracticeService>();
            var runContextAccessor = serviceProvider.GetRequiredService<IPromptOptRunContextAccessor>();
            var practices = (await practiceService.GetPracticesAsync()).ToList();

            var streamTranscript = HasFlag(args, "--transcript");
            var goal = ResolveGoal(args);

            var runId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
            var outDir = Path.Combine(".bendover", "promptopt", "runs", runId);
            runContextAccessor.Current = new PromptOptRunContext(
                outDir,
                Capture: true,
                RunId: runId,
                BundleId: "current",
                ApplySandboxPatchToSource: true,
                StreamTranscript: streamTranscript
            );
            AnsiConsole.MarkupLine($"[grey]run_id={Markup.Escape(runId)}[/]");
            AnsiConsole.MarkupLine($"[grey]artifacts={Markup.Escape(outDir)}[/]");

            AnsiConsole.MarkupLine("[bold purple]🎵take it easy, I will do the work...🎵[/]");

            await orchestrator.RunAsync(goal, practices);

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

    private static string ResolveGoal(string[] args)
    {
        var goalFromArgs = string.Join(
            " ",
            (args ?? Array.Empty<string>()).Where(a => !IsControlFlag(a)))
            .Trim();

        if (!string.IsNullOrWhiteSpace(goalFromArgs))
        {
            return goalFromArgs;
        }

        return AnsiConsole.Ask<string>("[bold yellow]What do you want to build?[/]");
    }

    private static bool HasFlag(string[] args, string flag)
    {
        return (args ?? Array.Empty<string>())
            .Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsControlFlag(string arg)
    {
        return ControlFlags.Any(flag => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildLmSummary(AgentOptions options)
    {
        var hasChatGptSession = false;
        try
        {
            hasChatGptSession = new ChatGptAuthStore().Load() is not null;
        }
        catch
        {
            hasChatGptSession = false;
        }

        var useChatGptSubscription = string.IsNullOrWhiteSpace(options.ApiKey) && hasChatGptSession;
        var model = !string.IsNullOrWhiteSpace(options.Model)
            ? options.Model
            : useChatGptSubscription
                ? ChatGptDefaults.DefaultModel
                : "<unset-model>";

        if (useChatGptSubscription)
        {
            return $"ChatGPT subscription ({model})";
        }

        var endpoint = string.IsNullOrWhiteSpace(options.Endpoint) ? "<unset-endpoint>" : options.Endpoint;
        return $"endpoint mode ({model} @ {endpoint})";
    }
}
