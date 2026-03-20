using System.Text;
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
    private const int TranscriptPreviewLimit = 320;
    private const int PromptHistoryDepth = 5;

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
                string? lastFailureDigestForRunResult = null;
                var stepHistory = new List<TurnHistoryEntry>();
                var completed = false;
                var completionStep = 0;
                var lastScriptExitCode = (int?)null;
                var turnSettings = new AgenticTurnSettings();

                var turnPipeline = BuildTurnPipeline(
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
                        EngineerPromptTemplate = engineerPromptTemplate,
                        PracticesContext = practicesContext,
                        Plan = plan ?? string.Empty,
                        SelectedPracticeNames = selectedPracticeNames,
                        StepHistory = stepHistory,
                        TurnSettings = turnSettings
                    };

                    await turnPipeline(turnContext);
                    lastScriptExitCode = turnContext.LastScriptExitCode;

                    if (turnContext.UnhandledException is not null)
                    {
                        lastFailureDigestForRunResult = turnContext.FailureDigest;
                        await RecordRunResultAsync(
                            status: "failed_exception",
                            completionStep: null,
                            completionSignaled: false,
                            hasCodeChanges: false,
                            gitDiffBytes: 0,
                            lastScriptExitCode: lastScriptExitCode,
                            lastFailureDigest: lastFailureDigestForRunResult);
                        throw new InvalidOperationException(
                            $"Engineer step {stepNumber} failed.\n{lastFailureDigestForRunResult}",
                            turnContext.UnhandledException);
                    }

                    if (turnContext.StepFailed)
                    {
                        lastFailureDigestForRunResult = turnContext.FailureDigest;
                        continue;
                    }

                    if (turnContext.CompletionSignaled)
                    {
                        lastFailureDigestForRunResult = null;
                        completed = true;
                        completionStep = stepNumber;
                        break;
                    }

                    lastFailureDigestForRunResult = null;
                }

                if (!completed)
                {
                    await RecordRunResultAsync(
                        status: "failed_max_turns",
                        completionStep: null,
                        completionSignaled: false,
                        hasCodeChanges: false,
                        gitDiffBytes: 0,
                        lastScriptExitCode: lastScriptExitCode,
                        lastFailureDigest: lastFailureDigestForRunResult);
                    throw new InvalidOperationException($"Engineer failed after {MaxActionSteps} action steps.\n{lastFailureDigestForRunResult}");
                }

                var gitDiffContent = await PersistSandboxArtifactsAsync();
                await RecordRunResultAsync(
                    status: "completed",
                    completionStep: completionStep,
                    completionSignaled: true,
                    hasCodeChanges: !string.IsNullOrWhiteSpace(gitDiffContent),
                    gitDiffBytes: gitDiffContent.Length,
                    lastScriptExitCode: lastScriptExitCode,
                    lastFailureDigest: lastFailureDigestForRunResult);
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
        ITranscriptWriter transcriptWriter = streamTranscript
            ? new OrchestratorTranscriptWriter(this, selectedPracticeNames)
            : new NoOpTranscriptWriter();

        return new TurnBuilder((type, capabilities) => CreateTurnStep(
                type,
                capabilities,
                engineerClient,
                engineerPromptTemplate))
            .WithTranscript(transcriptWriter)
            .WithRunRecording(new RunRecordingOptions(
                RecordPrompt: true,
                RecordOutput: true,
                RecordArtifacts: false))
            .Add<TurnGuard>()
            .Add<BuildContext>()
            .Add<BuildPrompt>()
            .Add<AgentInvoke>()
            .Add<TurnExecute>()
            .Add<FinalizeTurn>()
            .Build();
    }

    private TurnStep CreateTurnStep(
        Type type,
        TurnCapabilities capabilities,
        IChatClient engineerClient,
        string engineerPromptTemplate)
    {
        if (type == typeof(TurnGuard))
        {
            return new TurnGuard(this, capabilities);
        }

        if (type == typeof(BuildContext))
        {
            return new BuildContext(this);
        }

        if (type == typeof(BuildPrompt))
        {
            return new BuildPrompt(this, engineerPromptTemplate);
        }

        if (type == typeof(AgentInvoke))
        {
            return new AgentInvoke(this, capabilities, engineerClient);
        }

        if (type == typeof(TurnExecute))
        {
            return new TurnExecute(_agenticTurnService);
        }

        if (type == typeof(FinalizeTurn))
        {
            return new FinalizeTurn(this, capabilities);
        }

        throw new InvalidOperationException($"Unsupported turn step type: {type.FullName}");
    }

    private static List<ChatMessage> BuildEngineerMessages(
        string engineerPromptTemplate,
        string practicesContext,
        string plan,
        string contextBlock)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                $"{engineerPromptTemplate}\n\n" +
                $"Selected Practices:\n{practicesContext}"),
            new(ChatRole.User, $"Plan: {plan}")
        };

        if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            messages.Add(new ChatMessage(ChatRole.User, contextBlock));
        }

        return messages;
    }

    private static string BuildContextBlock(IReadOnlyList<TurnHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return string.Empty;
        }

        var historyBuilder = new StringBuilder();
        historyBuilder.AppendLine($"Recent step history (oldest to newest, last {PromptHistoryDepth}):");
        foreach (var entry in history)
        {
            historyBuilder.AppendLine($"Step {entry.StepNumber} observation (raw):");
            historyBuilder.AppendLine(entry.ObservationContext);
            if (!string.IsNullOrWhiteSpace(entry.FailureContext))
            {
                historyBuilder.AppendLine($"Step {entry.StepNumber} failure (raw):");
                historyBuilder.AppendLine(entry.FailureContext);
            }

            historyBuilder.AppendLine();
        }

        return historyBuilder.ToString().TrimEnd();
    }

    private async Task RecordStepFailureAsync(int stepNumber, string failureDigest)
    {
        var failurePhase = BuildStepPhase("engineer_step_failure", stepNumber);
        await _runRecorder.RecordOutputAsync(failurePhase, failureDigest);
    }

    private async Task EmitTranscriptPromptAsync(
        string phase,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyCollection<string> selectedPractices)
    {
        var roles = string.Join(",", messages.Select(m => m.Role.Value));
        var userPrompt = messages
            .Where(m => string.Equals(m.Role.Value, ChatRole.User.Value, StringComparison.OrdinalIgnoreCase))
            .Select(m => ToCompactSingleLine(m.Text))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        var userSummary = userPrompt.Length == 0 ? "(none)" : string.Join(" | ", userPrompt);

        var systemPrompt = messages
            .FirstOrDefault(m => string.Equals(m.Role.Value, ChatRole.System.Value, StringComparison.OrdinalIgnoreCase))
            ?.Text ?? string.Empty;
        var deliveredPractices = ExtractPracticesFromSystemPrompt(systemPrompt);
        var deliveredCsv = deliveredPractices.Count == 0
            ? "(none)"
            : string.Join(", ", deliveredPractices.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

        await NotifyProgressAsync($"[transcript][prompt] phase={phase} roles={roles} user={userSummary} system_selected_practices={deliveredCsv}");

        foreach (var practice in selectedPractices
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var delivered = deliveredPractices.Contains(practice) ? "yes" : "no";
            await NotifyProgressAsync($"[transcript][audit] phase={phase} practice={practice} delivered={delivered}");
        }
    }

    private async Task EmitTranscriptOutputAsync(string phase, string output)
    {
        var preview = ToCompactSingleLine(output);
        await NotifyProgressAsync($"[transcript][output] phase={phase} chars={output.Length} preview={preview}");
    }

    private async Task EmitTranscriptFailureAsync(string phase, string failureDigest)
    {
        await NotifyProgressAsync($"[transcript][failure] phase={phase}\n{failureDigest}");
    }

    private static HashSet<string> ExtractPracticesFromSystemPrompt(string systemPrompt)
    {
        var delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(systemPrompt))
        {
            return delivered;
        }

        const string marker = "Selected Practices:";
        var markerIndex = systemPrompt.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return delivered;
        }

        var lines = systemPrompt
            .Substring(markerIndex + marker.Length)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- [", StringComparison.Ordinal))
            {
                continue;
            }

            var endBracket = line.IndexOf(']', 3);
            if (endBracket <= 3)
            {
                continue;
            }

            var name = line.Substring(3, endBracket - 3).Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                delivered.Add(name);
            }
        }

        return delivered;
    }

    private static string ToCompactSingleLine(string? text, int maxLength = TranscriptPreviewLimit)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(none)";
        }

        var singleLine = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Trim();

        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return $"{singleLine[..maxLength]}...(truncated)";
    }

    private static void AppendStepHistory(
        List<TurnHistoryEntry> stepHistory,
        int stepNumber,
        string observationContext,
        string? failureContext)
    {
        stepHistory.Add(new TurnHistoryEntry(stepNumber, observationContext, failureContext));
        var overflow = stepHistory.Count - PromptHistoryDepth;
        if (overflow > 0)
        {
            stepHistory.RemoveRange(0, overflow);
        }
    }

    private static string BuildExceptionObservationContext(Exception exception)
    {
        return $"exception_type={exception.GetType().FullName ?? "n/a"}\n" +
               $"exception_message={exception.Message}\n" +
               $"exception:\n{exception}";
    }

    private static string BuildTurnFailureDigest(
        AgenticTurnObservation observation,
        IReadOnlyCollection<string> failedChecks)
    {
        var gateSummary = failedChecks.Count == 0
            ? "(none)"
            : string.Join(", ", failedChecks);
        var scriptTail = GetLastLines(observation.ScriptExecution.CombinedOutput, 40);
        var diffTail = GetLastLines(observation.DiffExecution.CombinedOutput, 40);

        return $"failed_checks={gateSummary}\n" +
               $"completion_signaled={ToBoolLiteral(observation.CompletionSignaled)}\n" +
               $"tool_call={observation.ToolCall ?? "(none)"}\n" +
               $"script_exit_code={observation.ScriptExecution.ExitCode}\n" +
               $"diff_exit_code={observation.DiffExecution.ExitCode}\n" +
               $"script_output_tail:\n{scriptTail}\n\n" +
               $"diff_output_tail:\n{diffTail}";
    }

    private static string BuildStepTool(AgenticTurnObservation observation)
    {
        if (!string.IsNullOrWhiteSpace(observation.ToolCall))
        {
            return observation.ToolCall!;
        }

        if (observation.CompletionSignaled)
        {
            return "sdk.Done()";
        }

        return "(none)";
    }

    private static string BuildStepObservationSummary(AgenticTurnObservation observation)
    {
        return $"completion_signaled={ToBoolLiteral(observation.CompletionSignaled)}; " +
               $"script_exit={observation.ScriptExecution.ExitCode}; " +
               $"has_changes={observation.HasChanges}; " +
               $"diff_exit={observation.DiffExecution.ExitCode}; " +
               $"is_done={ToBoolLiteral(observation.CompletionSignaled)}";
    }

    private static string BuildFailureDigest(int exitCode, Exception? exception, string combinedOutput)
    {
        var exceptionType = exception?.GetType().FullName ?? "n/a";
        var exceptionMessage = exception?.Message ?? "n/a";
        var tail = GetLastLines(combinedOutput, 40);
        return $"exit_code={exitCode}\nexception_type={exceptionType}\nexception_message={exceptionMessage}\noutput_tail:\n{tail}";
    }

    private static string BuildStepPhase(string basePhase, int stepNumber)
    {
        return $"{basePhase}_{stepNumber}";
    }

    private static string ToBoolLiteral(bool value)
    {
        return value ? "true" : "false";
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

    private static string GetLastLines(string text, int maxLines)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);

        if (lines.Length <= maxLines)
        {
            return text;
        }

        return string.Join('\n', lines.Skip(lines.Length - maxLines));
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

    private sealed class OrchestratorTranscriptWriter : ITranscriptWriter
    {
        private readonly AgentOrchestrator _owner;
        private readonly IReadOnlyCollection<string> _selectedPractices;

        public OrchestratorTranscriptWriter(AgentOrchestrator owner, IReadOnlyCollection<string> selectedPractices)
        {
            _owner = owner;
            _selectedPractices = selectedPractices;
        }

        public Task WritePromptAsync(string phase, IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<string> selectedPractices)
        {
            return _owner.EmitTranscriptPromptAsync(phase, messages, selectedPractices.Count == 0 ? _selectedPractices : selectedPractices);
        }

        public Task WriteOutputAsync(string phase, string output)
        {
            return _owner.EmitTranscriptOutputAsync(phase, output);
        }

        public Task WriteFailureAsync(string phase, string failureDigest)
        {
            return _owner.EmitTranscriptFailureAsync(phase, failureDigest);
        }
    }

    private sealed class TurnGuard : TurnStep
    {
        private readonly AgentOrchestrator _owner;
        private readonly TurnCapabilities _capabilities;

        public TurnGuard(AgentOrchestrator owner, TurnCapabilities capabilities)
        {
            _owner = owner;
            _capabilities = capabilities;
        }

        public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                context.UnhandledException = ex;
                context.FailureDigest = BuildFailureDigest(
                    exitCode: 1,
                    exception: ex,
                    combinedOutput: ex.ToString());

                if (_capabilities.RunRecording.RecordOutput)
                {
                    await _owner.RecordStepFailureAsync(context.StepNumber, context.FailureDigest);
                }

                await _capabilities.TranscriptWriter.WriteFailureAsync(context.FailurePhase, context.FailureDigest);
            }
        }
    }

    private sealed class BuildContext : TurnStep
    {
        public BuildContext(AgentOrchestrator owner)
        {
        }

        public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
        {
            context.ContextBlock = BuildContextBlock(context.StepHistory);
            await next(context);

            if (context.UnhandledException is not null)
            {
                AppendStepHistory(
                    context.StepHistory,
                    context.StepNumber,
                    BuildExceptionObservationContext(context.UnhandledException),
                    context.FailureDigest);
                return;
            }

            if (context.StepFailed)
            {
                AppendStepHistory(
                    context.StepHistory,
                    context.StepNumber,
                    context.SerializedObservation,
                    context.FailureDigest);
                return;
            }

            if (!string.IsNullOrWhiteSpace(context.SerializedObservation))
            {
                AppendStepHistory(
                    context.StepHistory,
                    context.StepNumber,
                    context.SerializedObservation,
                    failureContext: null);
            }
        }
    }

    private sealed class BuildPrompt : TurnStep
    {
        private readonly string _engineerPromptTemplate;

        public BuildPrompt(AgentOrchestrator owner, string engineerPromptTemplate)
        {
            _engineerPromptTemplate = engineerPromptTemplate;
        }

        public override Task InvokeAsync(TurnContext context, TurnDelegate next)
        {
            context.EngineerMessages = BuildEngineerMessages(
                _engineerPromptTemplate,
                context.PracticesContext,
                context.Plan,
                context.ContextBlock);

            return next(context);
        }
    }

    private sealed class AgentInvoke : TurnStep
    {
        private readonly AgentOrchestrator _owner;
        private readonly TurnCapabilities _capabilities;
        private readonly IChatClient _engineerClient;

        public AgentInvoke(AgentOrchestrator owner, TurnCapabilities capabilities, IChatClient engineerClient)
        {
            _owner = owner;
            _capabilities = capabilities;
            _engineerClient = engineerClient;
        }

        public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
        {
            if (_capabilities.RunRecording.RecordPrompt)
            {
                await _owner._runRecorder.RecordPromptAsync(context.EngineerPhase, context.EngineerMessages);
            }

            await _capabilities.TranscriptWriter.WritePromptAsync(
                context.EngineerPhase,
                context.EngineerMessages,
                context.SelectedPracticeNames);

            var actorCodeResponse = await _engineerClient.CompleteAsync(context.EngineerMessages);
            context.ActorCode = actorCodeResponse.Message.Text ?? string.Empty;

            if (_capabilities.RunRecording.RecordOutput)
            {
                await _owner._runRecorder.RecordOutputAsync(context.EngineerPhase, context.ActorCode);
            }

            await _capabilities.TranscriptWriter.WriteOutputAsync(context.EngineerPhase, context.ActorCode);
            await next(context);
        }
    }

    private sealed class TurnExecute : TurnStep
    {
        private readonly IAgenticTurnService _agenticTurnService;

        public TurnExecute(IAgenticTurnService agenticTurnService)
        {
            _agenticTurnService = agenticTurnService;
        }

        public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
        {
            context.Observation = await _agenticTurnService.ExecuteAgenticTurnAsync(context.ActorCode, context.TurnSettings);
            context.LastScriptExitCode = context.Observation.ScriptExecution.ExitCode;
            await next(context);
        }
    }

    private sealed class FinalizeTurn : TurnStep
    {
        private readonly AgentOrchestrator _owner;
        private readonly TurnCapabilities _capabilities;
        public FinalizeTurn(AgentOrchestrator owner, TurnCapabilities capabilities)
        {
            _owner = owner;
            _capabilities = capabilities;
        }

        public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
        {
            var observation = context.Observation
                ?? throw new InvalidOperationException("Turn observation was not produced.");

            context.SerializedObservation = JsonSerializer.Serialize(observation);

            if (_capabilities.RunRecording.RecordOutput)
            {
                await _owner._runRecorder.RecordOutputAsync(context.ObservationPhase, context.SerializedObservation);
            }

            await _capabilities.TranscriptWriter.WriteOutputAsync(context.ObservationPhase, context.SerializedObservation);
            await _owner.NotifyStepAsync(new AgentStepEvent(
                StepNumber: context.StepNumber,
                Plan: observation.StepPlan,
                Tool: BuildStepTool(observation),
                Observation: BuildStepObservationSummary(observation),
                IsCompletion: observation.CompletionSignaled));

            if (observation.ScriptExecution.ExitCode != 0)
            {
                context.FailureDigest = BuildTurnFailureDigest(observation, new[] { "script_exit_non_zero" });
                context.StepFailed = true;

                if (_capabilities.RunRecording.RecordOutput)
                {
                    await _owner.RecordStepFailureAsync(context.StepNumber, context.FailureDigest);
                }

                await _capabilities.TranscriptWriter.WriteFailureAsync(context.FailurePhase, context.FailureDigest);
                return;
            }

            context.CompletionSignaled = observation.CompletionSignaled;
            await next(context);
        }
    }
}
