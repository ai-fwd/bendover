using System.Text;
using System.Text.Json;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private const int MaxActionSteps = 24;
    private const int TranscriptPreviewLimit = 320;
    private const int PromptHistoryDepth = 5;
    private const int HistoryPreviewLimit = 140;

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

    private sealed record EngineerStepHistoryEntry(
        int StepNumber,
        string ObservationSummary,
        string? FailureSummary);

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

        // Capture Run Start and enforce a reproducible sandbox baseline.
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
            // 0. Environment Verification
            await NotifyProgressAsync("Verifying Environment...");
            await _environmentValidator.ValidateAsync();

            // 1a. Lead Phase (Practice Selection)
            await NotifyProgressAsync("Lead Agent Analyzing Request...");
            var selectedPracticeNames = (await _leadAgent.AnalyzeTaskAsync(initialGoal, allPractices, agentsPath)).ToArray();
            var unknownSelectedPractices = selectedPracticeNames
                .Where(name => !availablePracticeNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Record Lead Input
            var leadMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, $"Goal: {initialGoal}") };
            await _runRecorder.RecordPromptAsync("lead", leadMessages);

            // Output is the list of selected practices
            // Serialization logic is implicit here but for recording output we likely want the string representation
            await _runRecorder.RecordOutputAsync("lead", JsonSerializer.Serialize(selectedPracticeNames));

            if (selectedPracticeNames.Length == 0)
            {
                var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    $"Lead selected no practices. Available practices: [{availableCsv}]");
            }

            if (unknownSelectedPractices.Length > 0)
            {
                var unknownCsv = string.Join(", ", unknownSelectedPractices);
                var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    $"Lead selected unknown practices: [{unknownCsv}]. Available practices: [{availableCsv}]");
            }

            var selectedNameSet = new HashSet<string>(selectedPracticeNames, StringComparer.OrdinalIgnoreCase);
            var selectedPractices = allPractices.Where(p => selectedNameSet.Contains(p.Name)).ToList();

            // Format practices for prompt
            var practicesContext = string.Join("\n", selectedPractices.Select(p => $"- [{p.Name}] ({p.AreaOfConcern}): {p.Content}"));

            // Removed RecordPracticesAsync call

            // 2. Planning Phase (Architect) is temporarily disabled.
            // await NotifyAsync("Architect Planning...");
            // var architectClient = _clientResolver.GetClient(AgentRole.Architect);
            // var architectMessages = new List<ChatMessage>
            // {
            //     new ChatMessage(ChatRole.System, $"You are an Architect.\n\nSelected Practices:\n{practicesContext}"),
            //     new ChatMessage(ChatRole.User, $"Goal: {initialGoal}")
            // };
            // await _runRecorder.RecordPromptAsync("architect", architectMessages);
            // var planResponse = await architectClient.CompleteAsync(architectMessages);
            // var plan = planResponse.Message.Text;
            // await _runRecorder.RecordOutputAsync("architect", plan ?? "");

            // Keep this as the effective plan context so Engineer still receives task direction.
            var plan = initialGoal;

            var engineerClient = _clientResolver.GetClient(AgentRole.Engineer);
            // var reviewerClient = _clientResolver.GetClient(AgentRole.Reviewer);
            var engineerPromptTemplate = _agentPromptService.LoadEngineerPromptTemplate(agentsPath);

            // 3. Step-wise execution loop
            await NotifyProgressAsync("Executing in Container...");
            await _containerService.StartContainerAsync(new SandboxExecutionSettings(
                Directory.GetCurrentDirectory(),
                BaseCommit: baseCommit));
            try
            {
                string? lastFailureDigestForRunResult = null;
                var stepHistory = new List<EngineerStepHistoryEntry>();
                var completed = false;
                var completionStep = 0;
                string? completionActionKind = null;
                var lastScriptExitCode = (int?)null;
                var lastChangedFilesCount = 0;
                var turnSettings = new AgenticTurnSettings();

                for (var stepIndex = 0; stepIndex < MaxActionSteps; stepIndex++)
                {
                    var stepNumber = stepIndex + 1;
                    await NotifyProgressAsync($"Engineer step {stepNumber} of {MaxActionSteps}...");

                    var engineerMessages = BuildEngineerMessages(
                        engineerPromptTemplate,
                        practicesContext,
                        plan ?? string.Empty,
                        stepHistory);
                    var engineerPhase = BuildStepPhase("engineer_step", stepNumber);
                    await _runRecorder.RecordPromptAsync(engineerPhase, engineerMessages);
                    await EmitTranscriptPromptAsync(
                        context.StreamTranscript,
                        engineerPhase,
                        engineerMessages,
                        selectedPracticeNames);

                    var actorCodeResponse = await engineerClient.CompleteAsync(engineerMessages);
                    var actorCode = actorCodeResponse.Message.Text ?? string.Empty;
                    await _runRecorder.RecordOutputAsync(engineerPhase, actorCode);
                    await EmitTranscriptOutputAsync(context.StreamTranscript, engineerPhase, actorCode);

                    // Reviewer phase is temporarily disabled to keep the loop to Lead + Engineer only.
                    // await NotifyAsync("Reviewer Critiquing Code...");
                    // var reviewerMessages = new List<ChatMessage>
                    // {
                    //     new ChatMessage(ChatRole.System, $"You are a Reviewer.\n\nSelected Practices:\n{practicesContext}"),
                    //     new ChatMessage(ChatRole.User, $"Review this code: {actorCode}")
                    // };
                    // var reviewerPhase = BuildStepPhase("reviewer_step", stepNumber);
                    // await _runRecorder.RecordPromptAsync(reviewerPhase, reviewerMessages);
                    // var critiqueResponse = await reviewerClient.CompleteAsync(reviewerMessages);
                    // var critique = critiqueResponse.Message.Text;
                    // await _runRecorder.RecordOutputAsync(reviewerPhase, critique ?? "");

                    try
                    {
                        var turnObservation = await _agenticTurnService.ExecuteAgenticTurnAsync(actorCode, turnSettings);
                        var observationPhase = BuildStepPhase("agentic_step_observation", stepNumber);
                        var serializedObservation = JsonSerializer.Serialize(turnObservation);
                        await _runRecorder.RecordOutputAsync(observationPhase, serializedObservation);
                        await EmitTranscriptOutputAsync(context.StreamTranscript, observationPhase, serializedObservation);
                        var observationSummary = BuildCompactObservationSummary(turnObservation);
                        lastScriptExitCode = turnObservation.ScriptExecution.ExitCode;
                        lastChangedFilesCount = turnObservation.ChangedFiles.Length;
                        await NotifyStepAsync(new AgentStepEvent(
                            StepNumber: stepNumber,
                            Plan: turnObservation.StepPlan,
                            Tool: BuildStepTool(turnObservation),
                            Observation: BuildStepObservationSummary(turnObservation),
                            IsCompletion: turnObservation.Action.IsCompletionAction));

                        if (turnObservation.ScriptExecution.ExitCode != 0)
                        {
                            lastFailureDigestForRunResult = BuildTurnFailureDigest(
                                turnObservation,
                                new[] { "script_exit_non_zero" });
                            AppendStepHistory(
                                stepHistory,
                                stepNumber,
                                observationSummary,
                                BuildCompactFailureSummaryFromObservation(turnObservation, "script_exit_non_zero"));
                            await RecordStepFailureAsync(context.StreamTranscript, stepNumber, lastFailureDigestForRunResult);
                            continue;
                        }

                        AppendStepHistory(stepHistory, stepNumber, observationSummary, failureSummary: null);

                        if (turnObservation.Action.IsCompletionAction)
                        {
                            lastFailureDigestForRunResult = null;
                            completed = true;
                            completionStep = stepNumber;
                            completionActionKind = turnObservation.Action.KindToken;
                            break;
                        }

                        lastFailureDigestForRunResult = null;
                    }
                    catch (Exception ex)
                    {
                        lastFailureDigestForRunResult = BuildFailureDigest(
                            exitCode: 1,
                            exception: ex,
                            combinedOutput: ex.ToString());
                        AppendStepHistory(
                            stepHistory,
                            stepNumber,
                            BuildCompactExceptionObservationSummary(ex),
                            BuildCompactFailureSummaryFromException(ex));
                        await RecordStepFailureAsync(context.StreamTranscript, stepNumber, lastFailureDigestForRunResult);
                    }
                }

                if (!completed)
                {
                    var failedDiffContent = string.Empty;
                    try
                    {
                        var failedDiffResult = await _containerService.ExecuteCommandAsync("cd /workspace && git diff");
                        failedDiffContent = failedDiffResult.CombinedOutput;
                    }
                    catch
                    {
                        // Keep failure-path recording best-effort and do not mask the original max-turn failure.
                    }

                    await RecordRunResultAsync(
                        status: "failed_max_turns",
                        completionStep: null,
                        completionActionKind: null,
                        hasCodeChanges: !string.IsNullOrWhiteSpace(failedDiffContent),
                        changedFilesCount: lastChangedFilesCount,
                        gitDiffBytes: failedDiffContent.Length,
                        lastScriptExitCode: lastScriptExitCode,
                        lastFailureDigest: lastFailureDigestForRunResult);
                    throw new InvalidOperationException($"Engineer failed after {MaxActionSteps} action steps.\n{lastFailureDigestForRunResult}");
                }

                var gitDiffContent = await PersistSandboxArtifactsAsync();
                await RecordRunResultAsync(
                    status: "completed",
                    completionStep: completionStep,
                    completionActionKind: completionActionKind,
                    hasCodeChanges: !string.IsNullOrWhiteSpace(gitDiffContent),
                    changedFilesCount: lastChangedFilesCount,
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

    private static List<ChatMessage> BuildEngineerMessages(
        string engineerPromptTemplate,
        string practicesContext,
        string plan,
        IReadOnlyList<EngineerStepHistoryEntry> history)
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System,
                $"{engineerPromptTemplate}\n\n" +
                $"Selected Practices:\n{practicesContext}"),
            new ChatMessage(ChatRole.User, $"Plan: {plan}")
        };

        if (history.Count > 0)
        {
            var historyBuilder = new StringBuilder();
            historyBuilder.AppendLine($"Recent step history (oldest to newest, last {PromptHistoryDepth}):");
            foreach (var entry in history)
            {
                historyBuilder.AppendLine($"Step {entry.StepNumber} observation: {entry.ObservationSummary}");
                if (!string.IsNullOrWhiteSpace(entry.FailureSummary))
                {
                    historyBuilder.AppendLine($"Step {entry.StepNumber} failure: {entry.FailureSummary}");
                }
            }

            messages.Add(new ChatMessage(ChatRole.User, historyBuilder.ToString().TrimEnd()));
        }

        return messages;
    }

    private async Task RecordStepFailureAsync(bool streamTranscript, int stepNumber, string failureDigest)
    {
        var failurePhase = BuildStepPhase("engineer_step_failure", stepNumber);
        await _runRecorder.RecordOutputAsync(failurePhase, failureDigest);
        await EmitTranscriptFailureAsync(streamTranscript, failurePhase, failureDigest);
    }

    private async Task EmitTranscriptPromptAsync(
        bool enabled,
        string phase,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyCollection<string> selectedPractices)
    {
        if (!enabled)
        {
            return;
        }

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

    private async Task EmitTranscriptOutputAsync(bool enabled, string phase, string output)
    {
        if (!enabled)
        {
            return;
        }

        var preview = ToCompactSingleLine(output);
        await NotifyProgressAsync($"[transcript][output] phase={phase} chars={output.Length} preview={preview}");
    }

    private async Task EmitTranscriptFailureAsync(bool enabled, string phase, string failureDigest)
    {
        if (!enabled)
        {
            return;
        }

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
        List<EngineerStepHistoryEntry> stepHistory,
        int stepNumber,
        string observationSummary,
        string? failureSummary)
    {
        stepHistory.Add(new EngineerStepHistoryEntry(stepNumber, observationSummary, failureSummary));
        var overflow = stepHistory.Count - PromptHistoryDepth;
        if (overflow > 0)
        {
            stepHistory.RemoveRange(0, overflow);
        }
    }

    private static string BuildCompactObservationSummary(AgenticTurnObservation observation)
    {
        return $"action_kind={observation.Action.KindToken}; " +
               $"action_command={ToCompactSingleLine(observation.Action.Command, HistoryPreviewLimit)}; " +
               $"tool_call={ToCompactSingleLine(observation.ToolCall, HistoryPreviewLimit)}; " +
               $"step_plan={ToCompactSingleLine(observation.StepPlan, HistoryPreviewLimit)}; " +
               $"script_exit={observation.ScriptExecution.ExitCode}; " +
               $"verification_exit={observation.BuildExecution.ExitCode}; " +
               $"changed_files_count={observation.ChangedFiles.Length}; " +
               $"has_changes={observation.HasChanges}; " +
               $"build_passed={observation.BuildPassed}; " +
               $"output_preview={ToCompactSingleLine(observation.ScriptExecution.CombinedOutput, HistoryPreviewLimit)}";
    }

    private static string BuildCompactExceptionObservationSummary(Exception exception)
    {
        return $"action_kind=unknown; " +
               $"action_command=(none); " +
               $"tool_call=(none); " +
               $"step_plan=(none); " +
               $"script_exit=1; " +
               $"verification_exit=n/a; " +
               $"changed_files_count=0; " +
               $"has_changes=false; " +
               $"build_passed=false; " +
               $"output_preview={ToCompactSingleLine(exception.ToString(), HistoryPreviewLimit)}";
    }

    private static string BuildCompactFailureSummaryFromObservation(AgenticTurnObservation observation, string failureType)
    {
        return $"failure_type={failureType}; " +
               $"action_kind={observation.Action.KindToken}; " +
               $"script_exit={observation.ScriptExecution.ExitCode}; " +
               $"error_preview={ToCompactSingleLine(observation.ScriptExecution.CombinedOutput, HistoryPreviewLimit)}";
    }

    private static string BuildCompactFailureSummaryFromException(Exception exception)
    {
        return $"failure_type=exception; " +
               $"exception_type={exception.GetType().FullName ?? "n/a"}; " +
               $"exception_message={ToCompactSingleLine(exception.Message, HistoryPreviewLimit)}; " +
               $"error_preview={ToCompactSingleLine(exception.ToString(), HistoryPreviewLimit)}";
    }

    private static string BuildTurnFailureDigest(
        AgenticTurnObservation observation,
        IReadOnlyCollection<string> failedChecks)
    {
        var changedFiles = observation.ChangedFiles.Length == 0
            ? "(none)"
            : string.Join(", ", observation.ChangedFiles);
        var gateSummary = failedChecks.Count == 0
            ? "(none)"
            : string.Join(", ", failedChecks);
        var scriptTail = GetLastLines(observation.ScriptExecution.CombinedOutput, 40);
        var diffTail = GetLastLines(observation.DiffExecution.CombinedOutput, 40);
        var verificationTail = GetLastLines(observation.BuildExecution.CombinedOutput, 40);

        return $"failed_checks={gateSummary}\n" +
               $"action_kind={observation.Action.KindToken}\n" +
               $"action_command={observation.Action.Command ?? "(none)"}\n" +
               $"script_exit_code={observation.ScriptExecution.ExitCode}\n" +
               $"verification_exit_code={observation.BuildExecution.ExitCode}\n" +
               $"changed_files={changedFiles}\n" +
               $"script_output_tail:\n{scriptTail}\n\n" +
               $"diff_output_tail:\n{diffTail}\n\n" +
               $"verification_output_tail:\n{verificationTail}";
    }

    private static string BuildStepTool(AgenticTurnObservation observation)
    {
        if (!string.IsNullOrWhiteSpace(observation.ToolCall))
        {
            return observation.ToolCall!;
        }

        return observation.Action.Kind switch
        {
            AgenticStepActionKind.MutationWrite => "sdk.File.Write(...)",
            AgenticStepActionKind.MutationDelete => "sdk.File.Delete(...)",
            AgenticStepActionKind.VerificationBuild or AgenticStepActionKind.VerificationTest or AgenticStepActionKind.DiscoveryShell
                => string.IsNullOrWhiteSpace(observation.Action.Command)
                    ? "sdk.Shell.Execute(...)"
                    : $"sdk.Shell.Execute(\"{observation.Action.Command}\")",
            AgenticStepActionKind.Complete => "sdk.Signal.Done()",
            _ => observation.Action.Command ?? "(unknown)"
        };
    }

    private static string BuildStepObservationSummary(AgenticTurnObservation observation)
    {
        return $"action={observation.Action.KindToken}; " +
               $"script_exit={observation.ScriptExecution.ExitCode}; " +
               $"verification_exit={observation.BuildExecution.ExitCode}; " +
               $"changed_files={observation.ChangedFiles.Length}; " +
               $"has_changes={observation.HasChanges}; " +
               $"build_passed={observation.BuildPassed}";
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

    private async Task RecordRunResultAsync(
        string status,
        int? completionStep,
        string? completionActionKind,
        bool hasCodeChanges,
        int changedFilesCount,
        int gitDiffBytes,
        int? lastScriptExitCode,
        string? lastFailureDigest)
    {
        var runResult = new
        {
            status,
            completion_step = completionStep,
            completion_action_kind = completionActionKind,
            has_code_changes = hasCodeChanges,
            changed_files_count = changedFilesCount,
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
        var gitDiff = await PersistArtifactFromSandboxCommandAsync("cd /workspace && git diff", "git_diff.patch", "git_diff.patch");
        await PersistArtifactFromSandboxCommandAsync("cd /workspace && dotnet build Bendover.sln", "dotnet_build.txt", "dotnet_build_error.txt");
        await PersistArtifactFromSandboxCommandAsync("cd /workspace && dotnet test", "dotnet_test.txt", "dotnet_test_error.txt");
        return gitDiff;
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
