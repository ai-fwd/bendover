using System.Text.Json;
using Bendover.Application.Interfaces;
using Bendover.Application.Turn;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private const int MaxActionSteps = 100;

    private readonly IAgentPromptService _agentPromptService;
    private readonly IChatClientResolver _clientResolver;
    private readonly IAgenticTurnService _agenticTurnService;
    private readonly IContainerService _containerService;
    private readonly IEnvironmentValidator _environmentValidator;
    private readonly ILeadAgent _leadAgent;
    private readonly IPromptOptRunRecorder _runRecorder;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;
    private readonly IGitRunner _gitRunner;

    private readonly IEnumerable<IAgentObserver> _observers;

    public AgentOrchestrator(
        IAgentPromptService agentPromptService,
        IChatClientResolver clientResolver,
        IContainerService containerService,
        ScriptGenerator scriptGenerator,
        IAgenticTurnService agenticTurnService,
        IEnvironmentValidator environmentValidator,
        IEnumerable<IAgentObserver> observers,
        ILeadAgent leadAgent,
        IPromptOptRunRecorder runRecorder,
        IPromptOptRunContextAccessor runContextAccessor,
        IGitRunner gitRunner)
    {
        _agentPromptService = agentPromptService;
        _clientResolver = clientResolver;
        _agenticTurnService = agenticTurnService;
        _containerService = containerService;
        _environmentValidator = environmentValidator;
        _observers = observers;
        _leadAgent = leadAgent;
        _runRecorder = runRecorder;
        _runContextAccessor = runContextAccessor;
        _gitRunner = gitRunner;
    }

    private async Task EmitEventAsync(AgentEvent evt)
    {
        foreach (var observer in _observers)
        {
            await observer.OnEventAsync(evt);
        }
    }

    private Task NotifyProgressAsync(string message)
    {
        return EmitEventAsync(new AgentProgressEvent(message));
    }

    private Task NotifyStepAsync(AgentStepEvent stepEvent)
    {
        return EmitEventAsync(stepEvent);
    }

    public async Task RunAsync(string initialGoal, IReadOnlyCollection<Practice> practices, string? agentsPath = null)
    {
        if (practices is null)
        {
            throw new ArgumentNullException(nameof(practices));
        }

        var allPractices = practices.ToList();
        var availablePracticeNames = new HashSet<string>(
            allPractices.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        string baseCommit;
        try
        {
            baseCommit = (await _gitRunner.RunAsync("rev-parse HEAD")).Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to resolve base commit from git HEAD.", ex);
        }

        if (string.IsNullOrWhiteSpace(baseCommit))
        {
            throw new InvalidOperationException("Failed to resolve base commit from git HEAD: command returned an empty commit hash.");
        }

        var context = _runContextAccessor.Current
            ?? throw new InvalidOperationException("PromptOpt run context is not set.");
        var bundleId = context.BundleId
            ?? throw new InvalidOperationException("PromptOpt run context BundleId is not set.");

        await _runRecorder.StartRunAsync(initialGoal, baseCommit, bundleId);

        try
        {
            await NotifyProgressAsync("Verifying Environment...");
            await _environmentValidator.ValidateAsync();

            await NotifyProgressAsync("Lead Agent Analyzing Request...");
            var selectedPracticeNames = (await _leadAgent.AnalyzeTaskAsync(initialGoal, allPractices, agentsPath)).ToArray();
            var unknownSelectedPractices = selectedPracticeNames
                .Where(name => !availablePracticeNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var leadMessages = new List<ChatMessage> { new(ChatRole.User, $"Goal: {initialGoal}") };
            await _runRecorder.RecordPromptAsync("lead", leadMessages);
            await _runRecorder.RecordOutputAsync("lead", JsonSerializer.Serialize(selectedPracticeNames));

            if (selectedPracticeNames.Length == 0)
            {
                var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException($"Lead selected no practices. Available practices: [{availableCsv}]");
            }

            if (unknownSelectedPractices.Length > 0)
            {
                var unknownCsv = string.Join(", ", unknownSelectedPractices);
                var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException($"Lead selected unknown practices: [{unknownCsv}]. Available practices: [{availableCsv}]");
            }

            var selectedNameSet = new HashSet<string>(selectedPracticeNames, StringComparer.OrdinalIgnoreCase);
            var selectedPractices = allPractices.Where(p => selectedNameSet.Contains(p.Name)).ToList();
            var practicesContext = string.Join("\n", selectedPractices.Select(p => $"- [{p.Name}] ({p.AreaOfConcern}): {p.Content}"));

            var plan = initialGoal;
            var engineerClient = _clientResolver.GetClient(AgentRole.Engineer);
            var engineerPromptTemplate = _agentPromptService.LoadEngineerPromptTemplate(agentsPath);

            await NotifyProgressAsync("Executing in Container...");
            await _containerService.StartContainerAsync(new SandboxExecutionSettings(
                Directory.GetCurrentDirectory(),
                BaseCommit: baseCommit));

            try
            {
                var runContext = new TurnRunContext
                {
                    StepHistory = new List<TurnHistoryEntry>(),
                    TurnSettings = new AgenticTurnSettings()
                };

                var completed = false;
                var completionStep = 0;
                var runTurn = BuildTurnPipeline(
                    engineerClient,
                    engineerPromptTemplate,
                    context.StreamTranscript,
                    selectedPracticeNames);

                for (var stepIndex = 0; stepIndex < MaxActionSteps; stepIndex++)
                {
                    var stepNumber = stepIndex + 1;
                    await NotifyProgressAsync($"Engineer step {stepNumber} of {MaxActionSteps}...");

                    var turnContext = new TurnContext
                    {
                        StepNumber = stepNumber,
                        Run = runContext,
                        Plan = plan ?? string.Empty,
                        SelectedPractices = selectedPracticeNames,
                        PracticesContext = practicesContext
                    };

                    await runTurn(turnContext);

                    if (turnContext.Result.Kind == TurnResultKind.FailedRetryable)
                    {
                        continue;
                    }

                    if (turnContext.Result.Kind == TurnResultKind.Completed)
                    {
                        completed = true;
                        completionStep = stepNumber;
                        break;
                    }

                    if (turnContext.Result.Kind == TurnResultKind.FailedTerminal)
                    {
                        var failureDigest = turnContext.Result.FailureDigest ?? runContext.LastFailureDigest;
                        await RecordRunResultAsync(
                            status: "failed_exception",
                            completionStep: null,
                            completionSignaled: false,
                            hasCodeChanges: false,
                            gitDiffBytes: 0,
                            lastScriptExitCode: runContext.LastScriptExitCode,
                            lastFailureDigest: failureDigest);
                        throw new InvalidOperationException(
                            $"Engineer step {stepNumber} failed.\n{failureDigest}",
                            turnContext.Result.Exception);
                    }
                }

                if (!completed)
                {
                    await RecordRunResultAsync(
                        status: "failed_max_turns",
                        completionStep: null,
                        completionSignaled: false,
                        hasCodeChanges: false,
                        gitDiffBytes: 0,
                        lastScriptExitCode: runContext.LastScriptExitCode,
                        lastFailureDigest: runContext.LastFailureDigest);
                    throw new InvalidOperationException($"Engineer failed after {MaxActionSteps} action steps.\n{runContext.LastFailureDigest}");
                }

                var gitDiffContent = await PersistSandboxArtifactsAsync();
                await RecordRunResultAsync(
                    status: "completed",
                    completionStep: completionStep,
                    completionSignaled: true,
                    hasCodeChanges: !string.IsNullOrWhiteSpace(gitDiffContent),
                    gitDiffBytes: gitDiffContent.Length,
                    lastScriptExitCode: runContext.LastScriptExitCode,
                    lastFailureDigest: runContext.LastFailureDigest);
                await ApplySandboxPatchToSourceAsync(context, gitDiffContent);
            }
            finally
            {
                await _containerService.StopContainerAsync();
            }

            await NotifyProgressAsync("Finished.");
        }
        finally
        {
            await _runRecorder.FinalizeRunAsync();
        }
    }

    private TurnDelegate BuildTurnPipeline(
        IChatClient engineerClient,
        string engineerPromptTemplate,
        bool streamTranscript,
        IReadOnlyCollection<string> selectedPracticeNames)
    {
        return new TurnBuilder(
                (type, capabilities) => CreateTurnStep(
                    type,
                    capabilities,
                    engineerClient,
                    engineerPromptTemplate),
                NotifyProgressAsync)
            .WithTranscript(streamTranscript, selectedPracticeNames)
            .WithRunRecording(new RunRecordingOptions(
                RecordPrompt: true,
                RecordOutput: true,
                RecordArtifacts: true))
            .Add<GuardTurnStep>()
            .Add<BuildContextStep>()
            .Add<BuildPromptStep>()
            .Add<InvokeAgentStep>()
            .Add<ExecuteTurnStep>()
            .Add<FinalizeTurnStep>()
            .Build();
    }

    private TurnStep CreateTurnStep(
        Type type,
        TurnCapabilities capabilities,
        IChatClient engineerClient,
        string engineerPromptTemplate)
    {
        if (type == typeof(GuardTurnStep))
        {
            return new GuardTurnStep(capabilities, _runRecorder);
        }

        if (type == typeof(BuildContextStep))
        {
            return new BuildContextStep();
        }

        if (type == typeof(BuildPromptStep))
        {
            return new BuildPromptStep(engineerPromptTemplate);
        }

        if (type == typeof(InvokeAgentStep))
        {
            return new InvokeAgentStep(capabilities, _runRecorder, engineerClient);
        }

        if (type == typeof(ExecuteTurnStep))
        {
            return new ExecuteTurnStep(_agenticTurnService);
        }

        if (type == typeof(FinalizeTurnStep))
        {
            return new FinalizeTurnStep(capabilities, _runRecorder, NotifyStepAsync);
        }

        throw new InvalidOperationException($"Unsupported turn step type: {type.FullName}");
    }

    private async Task RecordRunResultAsync(
        string status,
        int? completionStep,
        bool completionSignaled,
        bool hasCodeChanges,
        int gitDiffBytes,
        int? lastScriptExitCode,
        string? lastFailureDigest)
    {
        var runResult = new
        {
            status,
            completion_step = completionStep,
            completion_signaled = completionSignaled,
            has_code_changes = hasCodeChanges,
            git_diff_bytes = gitDiffBytes,
            last_script_exit_code = lastScriptExitCode,
            last_failure_digest = lastFailureDigest
        };

        await _runRecorder.RecordArtifactAsync(
            "run_result.json",
            JsonSerializer.Serialize(runResult));
    }

    private async Task<string> PersistSandboxArtifactsAsync()
    {
        return await PersistArtifactFromSandboxCommandAsync("cd /workspace && git diff", "git_diff.patch", "git_diff.patch");
    }

    private async Task<string> PersistArtifactFromSandboxCommandAsync(string command, string successArtifactName, string errorArtifactName)
    {
        try
        {
            var result = await _containerService.ExecuteCommandAsync(command);
            var targetArtifact = result.ExitCode == 0 ? successArtifactName : errorArtifactName;
            await _runRecorder.RecordArtifactAsync(targetArtifact, result.CombinedOutput);
            return result.CombinedOutput;
        }
        catch (Exception ex)
        {
            await _runRecorder.RecordArtifactAsync(errorArtifactName, ex.ToString());
            return string.Empty;
        }
    }

    private async Task ApplySandboxPatchToSourceAsync(PromptOptRunContext context, string gitDiffContent)
    {
        if (!context.ApplySandboxPatchToSource)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(gitDiffContent))
        {
            return;
        }

        try
        {
            var checkOutput = await _gitRunner.RunAsync(
                "apply --check --whitespace=nowarn -",
                standardInput: gitDiffContent);
            await _runRecorder.RecordArtifactAsync(
                "host_apply_check.txt",
                string.IsNullOrWhiteSpace(checkOutput) ? "(no output)" : checkOutput);
        }
        catch (Exception ex)
        {
            await _runRecorder.RecordArtifactAsync("host_apply_check.txt", ex.ToString());
            throw new InvalidOperationException(
                $"Host patch apply failed at check stage.\n{ex.Message}",
                ex);
        }

        try
        {
            var applyOutput = await _gitRunner.RunAsync(
                "apply --whitespace=nowarn -",
                standardInput: gitDiffContent);
            await _runRecorder.RecordArtifactAsync(
                "host_apply_result.txt",
                string.IsNullOrWhiteSpace(applyOutput) ? "(no output)" : applyOutput);
        }
        catch (Exception ex)
        {
            await _runRecorder.RecordArtifactAsync("host_apply_result.txt", ex.ToString());
            throw new InvalidOperationException(
                $"Host patch apply failed at apply stage.\n{ex.Message}",
                ex);
        }
    }
}
