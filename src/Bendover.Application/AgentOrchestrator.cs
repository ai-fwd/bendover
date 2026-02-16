using System.Text.Json;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private const int MaxAgenticTurns = 4;
    private const int TranscriptPreviewLimit = 320;

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

    private async Task NotifyAsync(string message)
    {
        foreach (var observer in _observers)
        {
            await observer.OnProgressAsync(message);
        }
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
            await NotifyAsync("Verifying Environment...");
            await _environmentValidator.ValidateAsync();

            // 1a. Lead Phase (Practice Selection)
            await NotifyAsync("Lead Agent Analyzing Request...");
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

            // 3. Execution with retries
            await NotifyAsync("Executing in Container...");
            await _containerService.StartContainerAsync(new SandboxExecutionSettings(
                Directory.GetCurrentDirectory(),
                BaseCommit: baseCommit));
            try
            {
                string? failureDigest = null;
                var completed = false;
                var turnSettings = new AgenticTurnSettings();

                for (var attemptIndex = 0; attemptIndex < MaxAgenticTurns; attemptIndex++)
                {
                    var isRetry = attemptIndex > 0;
                    await NotifyAsync(isRetry
                        ? $"Engineer retry {attemptIndex} of {MaxAgenticTurns - 1}..."
                        : "Engineer Generating Code...");

                    var engineerMessages = BuildEngineerMessages(
                        engineerPromptTemplate,
                        practicesContext,
                        plan ?? string.Empty,
                        failureDigest);
                    var engineerPhase = BuildAttemptPhase("engineer", attemptIndex);
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
                    // var reviewerPhase = BuildAttemptPhase("reviewer", attemptIndex);
                    // await _runRecorder.RecordPromptAsync(reviewerPhase, reviewerMessages);
                    // var critiqueResponse = await reviewerClient.CompleteAsync(reviewerMessages);
                    // var critique = critiqueResponse.Message.Text;
                    // await _runRecorder.RecordOutputAsync(reviewerPhase, critique ?? "");

                    try
                    {
                        var turnObservation = await _agenticTurnService.ExecuteAgenticTurnAsync(actorCode, turnSettings);
                        var observationPhase = BuildAttemptPhase("agentic_turn_observation", attemptIndex);
                        var serializedObservation = JsonSerializer.Serialize(turnObservation);
                        await _runRecorder.RecordOutputAsync(observationPhase, serializedObservation);
                        await EmitTranscriptOutputAsync(context.StreamTranscript, observationPhase, serializedObservation);

                        if (turnObservation.ScriptExecution.ExitCode == 0
                            && turnObservation.HasChanges
                            && turnObservation.BuildPassed)
                        {
                            completed = true;
                            break;
                        }

                        failureDigest = BuildTurnFailureDigest(turnObservation);
                        var failurePhase = BuildAttemptPhase("engineer_failure_digest", attemptIndex);
                        await _runRecorder.RecordOutputAsync(failurePhase, failureDigest);
                        await EmitTranscriptFailureAsync(context.StreamTranscript, failurePhase, failureDigest);
                    }
                    catch (Exception ex)
                    {
                        failureDigest = BuildFailureDigest(
                            exitCode: 1,
                            exception: ex,
                            combinedOutput: ex.ToString());
                        var failurePhase = BuildAttemptPhase("engineer_failure_digest", attemptIndex);
                        await _runRecorder.RecordOutputAsync(failurePhase, failureDigest);
                        await EmitTranscriptFailureAsync(context.StreamTranscript, failurePhase, failureDigest);
                    }

                    if (attemptIndex == MaxAgenticTurns - 1)
                    {
                        throw new InvalidOperationException($"Engineer failed after {MaxAgenticTurns} turns.\n{failureDigest}");
                    }
                }

                if (!completed)
                {
                    throw new InvalidOperationException($"Engineer did not complete successfully.\n{failureDigest}");
                }

                var gitDiffContent = await PersistSandboxArtifactsAsync();
                await ApplySandboxPatchToSourceAsync(context, gitDiffContent);
            }
            finally
            {
                await _containerService.StopContainerAsync();
            }

            await NotifyAsync("Finished.");
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
        string? failureDigest)
    {
        var messages = new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System,
                $"{engineerPromptTemplate}\n\n" +
                $"Selected Practices:\n{practicesContext}"),
            new ChatMessage(ChatRole.User, $"Plan: {plan}")
        };

        if (!string.IsNullOrWhiteSpace(failureDigest))
        {
            messages.Add(new ChatMessage(ChatRole.User,
                "Previous attempt failed. Use this digest to fix the body and retry.\n" +
                $"Failure digest:\n{failureDigest}\n\n" +
                "Reminder: return only body statements."));
        }

        return messages;
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

        await NotifyAsync($"[transcript][prompt] phase={phase} roles={roles} user={userSummary} system_selected_practices={deliveredCsv}");

        foreach (var practice in selectedPractices
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var delivered = deliveredPractices.Contains(practice) ? "yes" : "no";
            await NotifyAsync($"[transcript][audit] phase={phase} practice={practice} delivered={delivered}");
        }
    }

    private async Task EmitTranscriptOutputAsync(bool enabled, string phase, string output)
    {
        if (!enabled)
        {
            return;
        }

        var preview = ToCompactSingleLine(output);
        await NotifyAsync($"[transcript][output] phase={phase} chars={output.Length} preview={preview}");
    }

    private async Task EmitTranscriptFailureAsync(bool enabled, string phase, string failureDigest)
    {
        if (!enabled)
        {
            return;
        }

        await NotifyAsync($"[transcript][failure] phase={phase}\n{failureDigest}");
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

    private static string ToCompactSingleLine(string? text)
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

        if (singleLine.Length <= TranscriptPreviewLimit)
        {
            return singleLine;
        }

        return $"{singleLine[..TranscriptPreviewLimit]}...(truncated)";
    }

    private static string BuildTurnFailureDigest(AgenticTurnObservation observation)
    {
        var failedGates = new List<string>();
        if (observation.ScriptExecution.ExitCode != 0)
        {
            failedGates.Add("script_exit_non_zero");
        }

        if (!observation.HasChanges)
        {
            failedGates.Add("empty_diff");
        }

        if (!observation.BuildPassed)
        {
            failedGates.Add("build_failed");
        }

        var changedFiles = observation.ChangedFiles.Length == 0
            ? "(none)"
            : string.Join(", ", observation.ChangedFiles);
        var gateSummary = failedGates.Count == 0
            ? "(none)"
            : string.Join(", ", failedGates);
        var scriptTail = GetLastLines(observation.ScriptExecution.CombinedOutput, 40);
        var buildTail = GetLastLines(observation.BuildExecution.CombinedOutput, 40);

        return $"failed_gates={gateSummary}\n" +
               $"script_exit_code={observation.ScriptExecution.ExitCode}\n" +
               $"build_exit_code={observation.BuildExecution.ExitCode}\n" +
               $"changed_files={changedFiles}\n" +
               $"script_output_tail:\n{scriptTail}\n\n" +
               $"build_output_tail:\n{buildTail}";
    }

    private static string BuildFailureDigest(int exitCode, Exception? exception, string combinedOutput)
    {
        var exceptionType = exception?.GetType().FullName ?? "n/a";
        var exceptionMessage = exception?.Message ?? "n/a";
        var tail = GetLastLines(combinedOutput, 40);
        return $"exit_code={exitCode}\nexception_type={exceptionType}\nexception_message={exceptionMessage}\noutput_tail:\n{tail}";
    }

    private static string BuildAttemptPhase(string basePhase, int attemptIndex)
    {
        if (attemptIndex == 0)
        {
            return basePhase;
        }

        return $"{basePhase}_retry_{attemptIndex}";
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

        await _gitRunner.RunAsync("apply --whitespace=nowarn -", standardInput: gitDiffContent);
    }
}
