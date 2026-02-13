using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;
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
            if (settings.Verbose)
            {
                Console.WriteLine($"[promptopt] Wrote evaluator: {targetPath}");
                PrintVerboseSummary(settings.Out);
            }
            return 0;
        }

        var services = new ServiceCollection();

        Env.TraversePath().Load();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddEnvironmentVariables()
            .Build();

        ProgramServiceRegistration.RegisterServices(services, configuration, Directory.GetCurrentDirectory());

        var serviceProvider = services.BuildServiceProvider();

        var fileSystem = serviceProvider.GetRequiredService<System.IO.Abstractions.IFileSystem>();
        var fileService = serviceProvider.GetRequiredService<IFileService>();
        var gitRunner = serviceProvider.GetRequiredService<IGitRunner>();
        var resolver = serviceProvider.GetRequiredService<IPromptBundleResolver>();
        var agentOrchestrator = serviceProvider.GetRequiredService<IAgentOrchestrator>();
        var runContextAccessor = serviceProvider.GetRequiredService<IPromptOptRunContextAccessor>();
        var runEvaluator = serviceProvider.GetRequiredService<IPromptOptRunEvaluator>();

        var orchestrator = new BenchmarkRunOrchestrator(
            gitRunner,
            agentOrchestrator,
            resolver,
            runEvaluator,
            fileSystem,
            runContextAccessor,
            fileService
        );
        var runScorer = new RunScoringOrchestrator(fileSystem, runEvaluator);

        if (!string.IsNullOrWhiteSpace(settings.RunId))
        {
            if (!string.IsNullOrWhiteSpace(settings.Task))
            {
                throw new InvalidOperationException("--task cannot be used with --run-id.");
            }

            if (!string.IsNullOrWhiteSpace(settings.Out))
            {
                throw new InvalidOperationException("--out cannot be used with --run-id.");
            }

            var outDir = await runScorer.ScoreAsync(
                settings.RunId,
                settings.Bundle,
                settings.Verbose
            );

            if (settings.Verbose)
            {
                PrintVerboseSummary(outDir);
            }

            return 0;
        }

        if (string.IsNullOrWhiteSpace(settings.Bundle) || string.IsNullOrWhiteSpace(settings.Task))
        {
            throw new InvalidOperationException("Bundle and Task are required unless --run-id is provided.");
        }

        if (string.IsNullOrWhiteSpace(settings.Out))
        {
            throw new InvalidOperationException("--out is required for bundle/task replay mode.");
        }

        await orchestrator.RunAsync(
            settings.Bundle,
            settings.Task,
            settings.Out,
            settings.Verbose
        );

        if (settings.Verbose)
        {
            PrintVerboseSummary(settings.Out);
        }

        return 0;
    }

    private static void PrintVerboseSummary(string outDir)
    {
        Console.WriteLine($"[promptopt] Out: {outDir}");

        PrintLeadSummary(outDir);
        PrintEvaluatorSummary(outDir);
    }

    private static void PrintLeadSummary(string outDir)
    {
        var outputsPath = Path.Combine(outDir, "outputs.json");
        if (!File.Exists(outputsPath))
        {
            Console.WriteLine("[promptopt] outputs.json: missing");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(outputsPath));
            if (!doc.RootElement.TryGetProperty("lead", out var leadElement))
            {
                Console.WriteLine("[promptopt] lead output: missing");
                return;
            }

            var selected = ExtractLeadSelections(leadElement);
            var selectedCsv = selected.Length == 0 ? "(none)" : string.Join(", ", selected);
            Console.WriteLine($"[promptopt] lead.selected_practices: {selectedCsv}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[promptopt] outputs.json parse error: {ex.Message}");
        }
    }

    private static string[] ExtractLeadSelections(JsonElement leadElement)
    {
        try
        {
            if (leadElement.ValueKind == JsonValueKind.Array)
            {
                return leadElement.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (leadElement.ValueKind == JsonValueKind.String)
            {
                var text = leadElement.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    return Array.Empty<string>();
                }

                using var leadDoc = JsonDocument.Parse(text);
                if (leadDoc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return Array.Empty<string>();
                }

                return leadDoc.RootElement.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
        catch
        {
            // Keep verbose diagnostics resilient. We print "(none)" when parsing fails.
        }

        return Array.Empty<string>();
    }

    private static void PrintEvaluatorSummary(string outDir)
    {
        var evaluatorPath = Path.Combine(outDir, "evaluator.json");
        if (!File.Exists(evaluatorPath))
        {
            Console.WriteLine("[promptopt] evaluator.json: missing");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(evaluatorPath));
            var root = doc.RootElement;

            var pass = root.TryGetProperty("pass", out var passElement)
                && passElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? passElement.GetBoolean()
                : false;

            var score = root.TryGetProperty("score", out var scoreElement)
                && scoreElement.ValueKind == JsonValueKind.Number
                ? scoreElement.GetDouble()
                : 0;

            Console.WriteLine($"[promptopt] evaluator.pass={pass} score={score:0.###}");

            if (!root.TryGetProperty("practice_attribution", out var practice)
                || practice.ValueKind != JsonValueKind.Object)
            {
                Console.WriteLine("[promptopt] evaluator.practice_attribution: missing");
                return;
            }

            var selected = ExtractStringArray(practice, "selected_practices");
            var offending = ExtractStringArray(practice, "offending_practices");
            Console.WriteLine($"[promptopt] evaluator.selected_practices: {(selected.Length == 0 ? "(none)" : string.Join(", ", selected))}");
            Console.WriteLine($"[promptopt] evaluator.offending_practices: {(offending.Length == 0 ? "(none)" : string.Join(", ", offending))}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[promptopt] evaluator.json parse error: {ex.Message}");
        }
    }

    private static string[] ExtractStringArray(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return element.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public class RunCommandSettings : CommandSettings
{
    [CommandOption("--run-id <ID>")]
    [Description("Score an existing run under .bendover/promptopt/runs/<ID>.")]
    public string? RunId { get; init; }

    [CommandOption("--bundle <PATH>")]
    [Description("Path to the bundle directory (override for --run-id, required for replay mode).")]
    public string? Bundle { get; init; }

    [CommandOption("--task <PATH>")]
    [Description("Path to the task directory (replay mode only).")]
    public string? Task { get; init; }

    [CommandOption("--out <PATH>")]
    [Description("Path to the output directory (replay mode only).")]
    public string? Out { get; init; }

    [CommandOption("--stub-eval-json <PATH>")]
    [Description("Write evaluator.json from a stub file and exit")]
    public string? StubEvalJson { get; init; }

    [CommandOption("--verbose")]
    [Description("Print lead/evaluator summary for manual inspection.")]
    public bool Verbose { get; init; }
}
