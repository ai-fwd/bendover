using System.ComponentModel;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Configuration;
using Bendover.Infrastructure.Services;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

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
        if (!string.IsNullOrWhiteSpace(settings.StubEvalJson))
        {
            if (string.IsNullOrWhiteSpace(settings.Out))
            {
                throw new InvalidOperationException("--out is required when using --stub-eval-json.");
            }

            Directory.CreateDirectory(settings.Out);
            var targetPath = Path.Combine(settings.Out, "evaluator.json");

            var jsonText = File.ReadAllText(settings.StubEvalJson);
            var node = JsonNode.Parse(jsonText) as JsonObject ?? new JsonObject();

            if (node.TryGetPropertyValue("score_if_contains", out var ruleNode) && ruleNode is JsonObject rule)
            {
                var file = rule["file"]?.GetValue<string>();
                var needle = rule["needle"]?.GetValue<string>();
                var score = rule["score"]?.GetValue<double>();

                if (!string.IsNullOrWhiteSpace(file) && !string.IsNullOrWhiteSpace(needle) && score.HasValue)
                {
                    if (!string.IsNullOrWhiteSpace(settings.Bundle))
                    {
                        var practicePath = Path.Combine(settings.Bundle, "practices", file);
                        if (File.Exists(practicePath))
                        {
                            var content = File.ReadAllText(practicePath);
                            if (content.Contains(needle, StringComparison.Ordinal))
                            {
                                node["score"] = score.Value;
                                node["pass"] = true;
                            }
                        }
                    }
                }
            }

            node.Remove("score_if_contains");
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(targetPath, node.ToJsonString(options));
            return 0;
        }

        var services = new ServiceCollection();

        Env.TraversePath().Load();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
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

    [CommandOption("--stub-eval-json <PATH>")]
    [Description("Write evaluator.json from a stub file and exit")]
    public string? StubEvalJson { get; init; }
}
