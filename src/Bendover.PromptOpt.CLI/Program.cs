using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure.ChatGpt;
using Bendover.Infrastructure.Configuration;
using Bendover.Presentation.Console;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Bendover.PromptOpt.CLI;

public static class Program
{
    public static Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "serve-chatgpt-proxy", StringComparison.OrdinalIgnoreCase))
        {
            var proxyApp = new CommandApp<ServeChatGptProxyCommand>();
            proxyApp.Configure(config => config.SetApplicationName("bendover-promptopt"));
            return proxyApp.RunAsync(args.Skip(1).ToArray());
        }

        if (args.Length > 0 && string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            args = args.Skip(1).ToArray();
        }

        var app = new CommandApp<RunCommand>();
        app.Configure(config => config.SetApplicationName("bendover-promptopt"));
        return app.RunAsync(args);
    }
}

public class RunCommand : AsyncCommand<RunCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RunCommandSettings settings, CancellationToken cancellationToken)
    {
        var requestedUiMode = settings.GetUiMode();
        var effectiveUiMode = ResolveEffectiveUiMode(requestedUiMode);
        if (requestedUiMode == PromptOptUiMode.Live
            && effectiveUiMode == PromptOptUiMode.Plain
            && settings.Verbose)
        {
            global::System.Console.WriteLine("[promptopt] ui.live requested but terminal is non-interactive; using plain output.");
        }

        Env.TraversePath().Load();

        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddEnvironmentVariables()
            .Build();
        var modelSummary = BuildLmSummary(configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions());
        var summaryReader = new PromptOptSummaryReader();
        var dashboard = effectiveUiMode == PromptOptUiMode.Live ? CreateDashboard() : null;

        if (!string.IsNullOrWhiteSpace(settings.StubEvalJson))
        {
            if (string.IsNullOrWhiteSpace(settings.Out))
            {
                throw new InvalidOperationException("--out is required when using --stub-eval-json.");
            }

            var outputDirectory = ResolvePath(settings.Out);
            var stubSucceeded = await ExecuteFlowAsync(
                dashboard,
                modelSummary,
                GetRunLabelFromPath(outputDirectory),
                outputDirectory,
                "Write evaluator.json from stub",
                settings.Verbose,
                bundleDir: settings.Bundle is null ? "(pending)" : ResolvePath(settings.Bundle),
                showExecutionPanel: false,
                includeLeadSummary: false,
                async sink =>
                {
                    sink.SetEvaluationSummary(PromptOptEvaluationSummary.Pending(
                        outputDirectory,
                        settings.Bundle is null ? "(pending)" : ResolvePath(settings.Bundle),
                        includeLeadSummary: false));
                    await RunStubModeAsync(settings, outputDirectory, sink);
                    var summary = summaryReader.Read(
                        outputDirectory,
                        settings.Bundle is null ? "(pending)" : ResolvePath(settings.Bundle),
                        includeLeadSummary: false);
                    sink.SetEvaluationSummary(summary);
                    WriteVerboseSummaryIfNeeded(dashboard, settings.Verbose, summaryReader, outputDirectory, includeLeadSummary: false);
                });
            return stubSucceeded ? 0 : 1;
        }

        var services = new ServiceCollection();
        ProgramServiceRegistration.RegisterServices(services, configuration, Directory.GetCurrentDirectory());
        RegisterAgentObserver(services, dashboard);

        using var serviceProvider = services.BuildServiceProvider();
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

            var runDirectory = ResolveRunDirectory(settings.RunId);
            var scoreGoal = LoadScoreGoal(runDirectory);
            var scoreSucceeded = await ExecuteFlowAsync(
                dashboard,
                modelSummary,
                settings.RunId.Trim(),
                runDirectory,
                scoreGoal,
                settings.Verbose,
                bundleDir: string.IsNullOrWhiteSpace(settings.Bundle) ? "(pending)" : ResolvePath(settings.Bundle),
                showExecutionPanel: false,
                includeLeadSummary: true,
                async sink =>
                {
                    sink.SetEvaluationSummary(PromptOptEvaluationSummary.Pending(
                        runDirectory,
                        string.IsNullOrWhiteSpace(settings.Bundle) ? "(pending)" : ResolvePath(settings.Bundle)));
                    var result = await runScorer.ScoreAsyncDetailed(
                        settings.RunId,
                        settings.Bundle,
                        settings.Verbose,
                        sink);

                    var summary = summaryReader.Read(result.RunDirectory, result.BundlePath);
                    sink.SetEvaluationSummary(summary);
                    WriteVerboseSummaryIfNeeded(dashboard, settings.Verbose, summaryReader, result.RunDirectory);
                });
            return scoreSucceeded ? 0 : 1;
        }

        if (string.IsNullOrWhiteSpace(settings.Bundle) || string.IsNullOrWhiteSpace(settings.Task))
        {
            throw new InvalidOperationException("Bundle and Task are required unless --run-id is provided.");
        }

        if (string.IsNullOrWhiteSpace(settings.Out))
        {
            throw new InvalidOperationException("--out is required for bundle/task replay mode.");
        }

        var replayOutputDirectory = ResolvePath(settings.Out);
        var replayGoal = LoadReplayGoal(settings.Task);

        var replayBundleDirectory = ResolvePath(settings.Bundle);
        var replaySucceeded = await ExecuteFlowAsync(
            dashboard,
            modelSummary,
            GetRunLabelFromPath(replayOutputDirectory),
            replayOutputDirectory,
            replayGoal,
            settings.Verbose,
            bundleDir: replayBundleDirectory,
            showExecutionPanel: true,
            includeLeadSummary: true,
            async sink =>
            {
                sink.SetEvaluationSummary(PromptOptEvaluationSummary.Pending(replayOutputDirectory, replayBundleDirectory));
                await orchestrator.RunAsync(
                    settings.Bundle,
                    settings.Task,
                    settings.Out,
                    settings.Verbose,
                    sink);

                var summary = summaryReader.Read(replayOutputDirectory, replayBundleDirectory);
                sink.SetEvaluationSummary(summary);
                WriteVerboseSummaryIfNeeded(dashboard, settings.Verbose, summaryReader, replayOutputDirectory);
            });

        return replaySucceeded ? 0 : 1;
    }

    private static PromptOptUiMode ResolveEffectiveUiMode(PromptOptUiMode requestedMode)
    {
        if (requestedMode == PromptOptUiMode.Live && !AnsiConsole.Profile.Capabilities.Interactive)
        {
            return PromptOptUiMode.Plain;
        }

        return requestedMode;
    }

    private static LiveCliDashboard CreateDashboard()
    {
        if (AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.Clear();
        }

        return new LiveCliDashboard();
    }

    private static void RegisterAgentObserver(IServiceCollection services, LiveCliDashboard? dashboard)
    {
        if (dashboard is not null)
        {
            services.AddSingleton(dashboard);
            services.AddSingleton<IAgentObserver, LiveCliDashboardObserver>();
            return;
        }

        services.AddSingleton<IAgentObserver, NoOpAgentObserver>();
    }

    private static async Task<bool> ExecuteFlowAsync(
        LiveCliDashboard? dashboard,
        string modelSummary,
        string runId,
        string runDir,
        string goal,
        bool verbose,
        string bundleDir,
        bool showExecutionPanel,
        bool includeLeadSummary,
        Func<IPromptOptCliStatusSink, Task> executeAsync)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);

        if (dashboard is null)
        {
            await executeAsync(new PlainPromptOptCliStatusSink(verbose));
            return true;
        }

        dashboard.Initialize(new LiveCliDashboardOptions(
            ModelSummary: modelSummary,
            RunId: runId,
            RunDir: runDir,
            Goal: goal,
            ShowEvaluationPanel: true,
            ShowExecutionPanel: showExecutionPanel,
            Subtitle: "Replay & Evaluate"));

        var sink = new LivePromptOptCliStatusSink(dashboard);
        sink.SetEvaluationSummary(PromptOptEvaluationSummary.Pending(runDir, bundleDir, includeLeadSummary));
        var succeeded = true;

        await dashboard.RunWithLiveAsync(async () =>
        {
            try
            {
                await executeAsync(sink);
                dashboard.MarkSuccess();
            }
            catch (Exception ex)
            {
                succeeded = false;
                var message = BuildErrorMessage(ex);
                sink.SetEvaluationSummary(PromptOptEvaluationSummary.Failed(runDir, message, bundleDir, includeLeadSummary));
                dashboard.MarkError(message);
            }
        });

        return succeeded;
    }

    private static async Task RunStubModeAsync(
        RunCommandSettings settings,
        string outputDirectory,
        IPromptOptCliStatusSink sink)
    {
        Directory.CreateDirectory(outputDirectory);
        var targetPath = Path.Combine(outputDirectory, "evaluator.json");

        sink.SetStatus("Writing evaluator.json");
        sink.SetEvaluationState(EvaluationPanelState.Running);

        var jsonText = File.ReadAllText(settings.StubEvalJson!);
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
        await File.WriteAllTextAsync(targetPath, node.ToJsonString(options));
        sink.SetStatus("evaluator.json written");
        if (settings.Verbose)
        {
            sink.AddVerboseDetail($"Wrote evaluator: {targetPath}");
        }
    }

    private static string ResolveRunDirectory(string runId)
    {
        return Path.GetFullPath(Path.Combine(
            Directory.GetCurrentDirectory(),
            ".bendover",
            "promptopt",
            "runs",
            runId.Trim()));
    }

    private static string ResolvePath(string path)
    {
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
    }

    private static string GetRunLabelFromPath(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fileName = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(fileName) ? "<unset-run-id>" : fileName;
    }

    private static string LoadReplayGoal(string taskPath)
    {
        var taskFilePath = Path.Combine(taskPath, "task.md");
        if (!File.Exists(taskFilePath))
        {
            return "Replay bundle evaluation";
        }

        var goal = File.ReadAllText(taskFilePath).Trim();
        return string.IsNullOrWhiteSpace(goal) ? "Replay bundle evaluation" : goal;
    }

    private static string LoadScoreGoal(string runDirectory)
    {
        var taskFilePath = Path.Combine(runDirectory, "task.md");
        if (!File.Exists(taskFilePath))
        {
            return "Score existing run";
        }

        var goal = File.ReadAllText(taskFilePath).Trim();
        return string.IsNullOrWhiteSpace(goal)
            ? "Score existing run"
            : $"Score existing run: {goal}";
    }

    private static void WriteVerboseSummaryIfNeeded(
        LiveCliDashboard? dashboard,
        bool verbose,
        PromptOptSummaryReader summaryReader,
        string outDir,
        bool includeLeadSummary = true)
    {
        if (dashboard is not null || !verbose)
        {
            return;
        }

        foreach (var line in summaryReader.GetVerboseSummaryLines(outDir, includeLeadSummary))
        {
            global::System.Console.WriteLine(line);
        }
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

    private static string BuildErrorMessage(Exception ex)
    {
        return string.IsNullOrWhiteSpace(ex.Message) ? ex.ToString() : ex.Message;
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

    [CommandOption("-u|--ui <MODE>")]
    [Description("Console UI mode: live or plain.")]
    public string Ui { get; init; } = "live";

    [CommandOption("--verbose")]
    [Description("Print lead/evaluator summary for manual inspection.")]
    public bool Verbose { get; init; }

    public override ValidationResult Validate()
    {
        return TryParseUiMode(Ui, out _)
            ? ValidationResult.Success()
            : ValidationResult.Error("--ui must be either 'live' or 'plain'.");
    }

    public PromptOptUiMode GetUiMode()
    {
        return TryParseUiMode(Ui, out var mode)
            ? mode
            : PromptOptUiMode.Live;
    }

    private static bool TryParseUiMode(string? value, out PromptOptUiMode mode)
    {
        if (Enum.TryParse(value, ignoreCase: true, out mode)
            && Enum.IsDefined(mode))
        {
            return true;
        }

        mode = PromptOptUiMode.Live;
        return false;
    }
}
