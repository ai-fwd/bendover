using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Bendover.Application.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Infrastructure.Services;

public class PromptOptRunRecorder : IPromptOptRunRecorder
{
    private readonly IFileSystem _fileSystem;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;
    private string? _runId;
    private string? _runDir;
    private bool _captureEnabled;

    private readonly Dictionary<string, List<ChatMessage>> _prompts = new();
    private readonly Dictionary<string, string> _outputs = new();
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PromptOptRunRecorder(
        IFileSystem fileSystem,
        IPromptOptRunContextAccessor runContextAccessor)
    {
        _fileSystem = fileSystem;
        _runContextAccessor = runContextAccessor;
    }

    public async Task<string> StartRunAsync(string goal, string baseCommit, string bundleId)
    {
        var context = _runContextAccessor.Current;
        if (context == null)
        {
            throw new InvalidOperationException("PromptOpt run context is not set.");
        }

        _runId = string.IsNullOrWhiteSpace(context.RunId)
            ?  $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}"
            : context.RunId;
        _runDir = context.OutDir;
        _captureEnabled = context.Capture;

        _fileSystem.Directory.CreateDirectory(_runDir);

        if (_captureEnabled)
        {
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "goal.txt"), goal);
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "base_commit.txt"), baseCommit);
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "bundle_id.txt"), bundleId);

            var meta = new
            {
                run_id = _runId,
                started_at = DateTime.UtcNow,
                base_commit = baseCommit,
                bundle_id = bundleId,
                goal_hash = goal.GetHashCode() // Simple hash for now
            };
            await WriteJsonAsync("run_meta.json", meta);
        }

        return _runId;
    }



    public Task RecordPromptAsync(string phase, List<ChatMessage> messages)
    {
        _prompts[phase] = new List<ChatMessage>(messages);
        return Task.CompletedTask;
    }

    public async Task RecordOutputAsync(string phase, string output)
    {
        _outputs[phase] = output;

        if (_captureEnabled && ShouldFlushLiveSnapshot(phase))
        {
            await PersistLiveSnapshotAsync();
        }
    }

    public async Task RecordArtifactAsync(string filename, string content)
    {
        if (!_captureEnabled || _runDir == null)
        {
            return;
        }

        var path = Path.Combine(_runDir, filename);
        await _fileSystem.File.WriteAllTextAsync(path, content);
    }

    public async Task FinalizeRunAsync()
    {
        if (_runDir == null || !_captureEnabled)
        {
            return;
        }

        await PersistLiveSnapshotAsync();
    }

    private string BuildTranscriptMarkdown()
    {
        var lines = new List<string>
        {
            "# PromptOpt Run Transcript",
            string.Empty,
            $"run_id: {_runId ?? "(unknown)"}",
            $"captured_at_utc: {DateTime.UtcNow:O}",
            string.Empty
        };

        AppendPracticeAudit(lines);

        lines.Add("## Phases");
        lines.Add(string.Empty);

        var phaseNames = OrderPhases(_prompts.Keys.Concat(_outputs.Keys));
        for (var phaseIndex = 0; phaseIndex < phaseNames.Count; phaseIndex++)
        {
            var phase = phaseNames[phaseIndex];
            lines.Add($"### {phase}");
            lines.Add(string.Empty);

            if (_prompts.TryGetValue(phase, out var messages))
            {
                var previousPromptPhase = FindPreviousPromptPhase(phaseNames, phaseIndex, _prompts);
                var previousMessages = previousPromptPhase == null
                    ? null
                    : _prompts[previousPromptPhase];
                var promptDelta = BuildPromptDelta(messages, previousMessages);

                if (promptDelta.Messages.Count == 0)
                {
                    if (previousPromptPhase != null)
                    {
                        lines.Add($"#### Prompt Delta (vs {previousPromptPhase})");
                        lines.Add(string.Empty);
                    }

                    lines.Add("(no new prompt content)");
                    lines.Add(string.Empty);
                }
                else
                {
                    foreach (var deltaMessage in promptDelta.Messages)
                    {
                        var suffixLabel = deltaMessage.IsSuffixDelta ? " (appended)" : string.Empty;
                        lines.Add($"#### Prompt Message {deltaMessage.MessageNumber}{suffixLabel}");
                        lines.Add($"role: {deltaMessage.Role}");
                        lines.Add(string.Empty);
                        AppendCodeBlock(lines, deltaMessage.Content);
                        lines.Add(string.Empty);
                    }
                }

                if (promptDelta.RemovedMessageCount > 0)
                {
                    lines.Add($"(removed prompt messages since previous phase: {promptDelta.RemovedMessageCount})");
                    lines.Add(string.Empty);
                }
            }

            if (_outputs.TryGetValue(phase, out var output))
            {
                if (TryParseEngineerStepObservation(phase, out _) && TryAppendObservationOutput(lines, output))
                {
                    continue;
                }

                lines.Add("#### Output");
                lines.Add(string.Empty);
                AppendCodeBlock(lines, output);
                lines.Add(string.Empty);
            }
        }

        return string.Join('\n', lines);
    }

    private static string? FindPreviousPromptPhase(
        IReadOnlyList<string> orderedPhases,
        int currentPhaseIndex,
        IReadOnlyDictionary<string, List<ChatMessage>> prompts)
    {
        for (var i = currentPhaseIndex - 1; i >= 0; i--)
        {
            var candidate = orderedPhases[i];
            if (prompts.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static PromptDelta BuildPromptDelta(
        IReadOnlyList<ChatMessage> currentMessages,
        IReadOnlyList<ChatMessage>? previousMessages)
    {
        if (previousMessages == null)
        {
            return new PromptDelta(
                currentMessages.Select((message, i) =>
                    new PromptDeltaMessage(
                        MessageNumber: i + 1,
                        Role: message.Role.Value,
                        Content: message.Text ?? string.Empty,
                        IsSuffixDelta: false)).ToList(),
                RemovedMessageCount: 0);
        }

        var deltas = new List<PromptDeltaMessage>();
        for (var i = 0; i < currentMessages.Count; i++)
        {
            var current = currentMessages[i];
            var currentText = current.Text ?? string.Empty;
            var currentRole = current.Role.Value;

            if (i >= previousMessages.Count)
            {
                deltas.Add(new PromptDeltaMessage(i + 1, currentRole, currentText, IsSuffixDelta: false));
                continue;
            }

            var previous = previousMessages[i];
            var previousText = previous.Text ?? string.Empty;
            var previousRole = previous.Role.Value;

            if (!string.Equals(currentRole, previousRole, StringComparison.OrdinalIgnoreCase))
            {
                deltas.Add(new PromptDeltaMessage(i + 1, currentRole, currentText, IsSuffixDelta: false));
                continue;
            }

            if (string.Equals(currentText, previousText, StringComparison.Ordinal))
            {
                continue;
            }

            if (currentText.StartsWith(previousText, StringComparison.Ordinal) && previousText.Length > 0)
            {
                var suffix = currentText[previousText.Length..];
                deltas.Add(new PromptDeltaMessage(i + 1, currentRole, suffix, IsSuffixDelta: true));
                continue;
            }

            deltas.Add(new PromptDeltaMessage(i + 1, currentRole, currentText, IsSuffixDelta: false));
        }

        var removedMessageCount = previousMessages.Count > currentMessages.Count
            ? previousMessages.Count - currentMessages.Count
            : 0;

        return new PromptDelta(deltas, removedMessageCount);
    }

    private void AppendPracticeAudit(List<string> lines)
    {
        lines.Add("## Practice Delivery Audit");
        lines.Add(string.Empty);

        var selectedPractices = ParseSelectedPracticesFromLeadOutput();
        var engineerPromptPhases = OrderPhases(_prompts.Keys)
            .Where(IsEngineerPromptPhase)
            .ToList();

        if (selectedPractices.Length == 0 || engineerPromptPhases.Count == 0)
        {
            lines.Add("(none)");
            lines.Add(string.Empty);
            return;
        }

        lines.Add("| phase | practice | delivered |");
        lines.Add("|---|---|---|");

        foreach (var phase in engineerPromptPhases)
        {
            var deliveredPractices = ExtractPracticesFromPrompt(_prompts[phase]);
            foreach (var practice in selectedPractices)
            {
                var delivered = deliveredPractices.Contains(practice) ? "yes" : "no";
                lines.Add($"| {phase} | {practice} | {delivered} |");
            }
        }

        lines.Add(string.Empty);
    }

    private string[] ParseSelectedPracticesFromLeadOutput()
    {
        if (!_outputs.TryGetValue("lead", out var leadOutput) || string.IsNullOrWhiteSpace(leadOutput))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(leadOutput);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return doc.RootElement
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static HashSet<string> ExtractPracticesFromPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var systemPrompt = messages
            .FirstOrDefault(m => string.Equals(m.Role.Value, ChatRole.System.Value, StringComparison.OrdinalIgnoreCase))
            ?.Text ?? string.Empty;

        return ExtractPracticesFromSystemPrompt(systemPrompt);
    }

    private static HashSet<string> ExtractPracticesFromSystemPrompt(string text)
    {
        var practices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return practices;
        }

        const string marker = "Selected Practices:";
        var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return practices;
        }

        var block = text
            .Substring(markerIndex + marker.Length)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);

        foreach (var rawLine in block)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("- [", StringComparison.Ordinal))
            {
                continue;
            }

            var end = line.IndexOf(']', 3);
            if (end <= 3)
            {
                continue;
            }

            var name = line.Substring(3, end - 3).Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                practices.Add(name);
            }
        }

        return practices;
    }

    private static bool IsEngineerPromptPhase(string phase)
    {
        if (string.Equals(phase, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryParseEngineerStepPrompt(phase, out _))
        {
            return true;
        }

        return phase.StartsWith("engineer_retry_", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> OrderPhases(IEnumerable<string> phases)
    {
        return phases
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetPhaseGroup)
            .ThenBy(GetPhaseAttempt)
            .ThenBy(GetPhaseStage)
            .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetPhaseGroup(string phase)
    {
        if (string.Equals(phase, "lead", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (TryParseEngineerPromptAttempt(phase, out _)
            || TryParseEngineerFailureAttempt(phase, out _)
            || TryParseEngineerStepPrompt(phase, out _)
            || TryParseEngineerStepObservation(phase, out _)
            || TryParseEngineerStepFailure(phase, out _))
        {
            return 1;
        }

        return 2;
    }

    private static int GetPhaseAttempt(string phase)
    {
        if (TryParseEngineerStepPrompt(phase, out var stepPrompt))
        {
            return stepPrompt;
        }

        if (TryParseEngineerStepObservation(phase, out var stepObservation))
        {
            return stepObservation;
        }

        if (TryParseEngineerStepFailure(phase, out var stepFailure))
        {
            return stepFailure;
        }

        if (TryParseEngineerPromptAttempt(phase, out var promptAttempt))
        {
            return promptAttempt;
        }

        if (TryParseEngineerFailureAttempt(phase, out var failureAttempt))
        {
            return failureAttempt;
        }

        return 0;
    }

    private static int GetPhaseStage(string phase)
    {
        if (TryParseEngineerStepPrompt(phase, out _)
            || TryParseEngineerPromptAttempt(phase, out _))
        {
            return 0;
        }

        if (TryParseEngineerStepObservation(phase, out _))
        {
            return 1;
        }

        if (TryParseEngineerStepFailure(phase, out _)
            || TryParseEngineerFailureAttempt(phase, out _))
        {
            return 2;
        }

        return 0;
    }

    private static bool TryParseEngineerStepPrompt(string phase, out int step)
    {
        const string prefix = "engineer_step_";
        if (phase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(phase[prefix.Length..], out step))
        {
            return true;
        }

        step = -1;
        return false;
    }

    private static bool TryParseEngineerStepObservation(string phase, out int step)
    {
        const string prefix = "agentic_step_observation_";
        if (phase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(phase[prefix.Length..], out step))
        {
            return true;
        }

        step = -1;
        return false;
    }

    private static bool TryParseEngineerStepFailure(string phase, out int step)
    {
        const string prefix = "engineer_step_failure_";
        if (phase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(phase[prefix.Length..], out step))
        {
            return true;
        }

        step = -1;
        return false;
    }

    private static bool TryParseEngineerPromptAttempt(string phase, out int attempt)
    {
        if (string.Equals(phase, "engineer", StringComparison.OrdinalIgnoreCase))
        {
            attempt = 0;
            return true;
        }

        const string prefix = "engineer_retry_";
        if (phase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(phase[prefix.Length..], out attempt))
        {
            return true;
        }

        attempt = -1;
        return false;
    }

    private static bool TryParseEngineerFailureAttempt(string phase, out int attempt)
    {
        if (string.Equals(phase, "engineer_failure_digest", StringComparison.OrdinalIgnoreCase))
        {
            attempt = 0;
            return true;
        }

        const string prefix = "engineer_failure_digest_retry_";
        if (phase.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(phase[prefix.Length..], out attempt))
        {
            return true;
        }

        attempt = -1;
        return false;
    }

    private static void AppendCodeBlock(List<string> lines, string text)
    {
        lines.Add("~~~text");
        lines.Add(text ?? string.Empty);
        lines.Add("~~~");
    }

    private static bool TryAppendObservationOutput(List<string> lines, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(output);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = doc.RootElement;

            lines.Add("#### Observation Summary");
            lines.Add(string.Empty);
            AppendObservationSummary(lines, root);
            lines.Add(string.Empty);

            lines.Add("#### SDK Actions");
            lines.Add(string.Empty);
            AppendSdkActions(lines, root);
            lines.Add(string.Empty);

            lines.Add("#### Raw Observation (JSON)");
            lines.Add(string.Empty);
            AppendCodeBlock(lines, JsonSerializer.Serialize(root, PrettyJsonOptions));
            lines.Add(string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendObservationSummary(List<string> lines, JsonElement root)
    {
        lines.Add($"completion_signaled: {GetBooleanText(root, "CompletionSignaled")}");
        lines.Add($"has_changes: {GetBooleanText(root, "HasChanges")}");
        lines.Add($"step_plan: {GetStringText(root, "StepPlan")}");
        lines.Add($"tool_call: {GetStringText(root, "ToolCall")}");

        var scriptExit = TryGetNestedInt32(root, "ScriptExecution", "ExitCode", out var scriptExitCode)
            ? scriptExitCode.ToString()
            : "(none)";
        var diffExit = TryGetNestedInt32(root, "DiffExecution", "ExitCode", out var diffExitCode)
            ? diffExitCode.ToString()
            : "(none)";

        lines.Add($"script_exit: {scriptExit}");
        lines.Add($"diff_exit: {diffExit}");

        var diffNote = TryGetNestedString(root, "DiffExecution", "CombinedOutput", out var diffCombinedOutput)
            ? ToCompactSingleLine(diffCombinedOutput, 220)
            : "(none)";
        lines.Add($"diff_note: {diffNote}");
    }

    private static void AppendSdkActions(List<string> lines, JsonElement root)
    {
        var stdout = TryGetNestedString(root, "ScriptExecution", "Stdout", out var stdoutValue)
            ? stdoutValue
            : string.Empty;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            lines.Add("(none)");
            return;
        }

        var parsedCount = 0;
        var unparsed = new List<string>();

        var rawLines = stdout
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in rawLines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (TryParseSdkActionLine(line, out var actionLine, out var payloadLine, out var errorLine))
            {
                parsedCount++;
                lines.Add($"{parsedCount}. {actionLine}");
                if (!string.IsNullOrWhiteSpace(payloadLine))
                {
                    lines.Add($"   payload: {payloadLine}");
                }

                if (!string.IsNullOrWhiteSpace(errorLine))
                {
                    lines.Add($"   error: {errorLine}");
                }

                continue;
            }

            unparsed.Add(line);
        }

        if (parsedCount == 0)
        {
            lines.Add("(none)");
        }

        if (unparsed.Count == 0)
        {
            return;
        }

        lines.Add(string.Empty);
        lines.Add("Unparsed stdout lines:");
        foreach (var line in unparsed)
        {
            lines.Add($"- {ToCompactSingleLine(line, 320)}");
        }
    }

    private static bool TryParseSdkActionLine(
        string line,
        out string actionLine,
        out string? payloadLine,
        out string? errorLine)
    {
        actionLine = string.Empty;
        payloadLine = null;
        errorLine = null;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var root = doc.RootElement;
            if (!TryGetPropertyIgnoreCase(root, "event_type", out var eventTypeElement)
                || eventTypeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var eventType = eventTypeElement.GetString() ?? "(unknown_event)";
            var methodName = TryGetPropertyIgnoreCase(root, "method_name", out var methodNameElement)
                             && methodNameElement.ValueKind == JsonValueKind.String
                ? methodNameElement.GetString() ?? "(unknown_method)"
                : "(unknown_method)";
            var elapsed = TryGetPropertyIgnoreCase(root, "elapsed_ms", out var elapsedElement)
                          && elapsedElement.ValueKind == JsonValueKind.Number
                          && elapsedElement.TryGetInt64(out var elapsedMs)
                ? $"{elapsedMs}ms"
                : "n/a";

            actionLine = $"{eventType} {methodName} elapsed={elapsed}";

            if (TryGetPropertyIgnoreCase(root, "payload_json", out var payloadElement)
                && payloadElement.ValueKind == JsonValueKind.String)
            {
                var rawPayload = payloadElement.GetString();
                if (!string.IsNullOrWhiteSpace(rawPayload))
                {
                    payloadLine = ToCompactSingleLine(TryFormatJsonString(rawPayload), 320);
                }
            }

            if (TryGetPropertyIgnoreCase(root, "error", out var errorElement)
                && errorElement.ValueKind != JsonValueKind.Null)
            {
                errorLine = ToCompactSingleLine(FormatError(errorElement), 320);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatError(JsonElement errorElement)
    {
        if (errorElement.ValueKind == JsonValueKind.Object)
        {
            var type = TryGetPropertyIgnoreCase(errorElement, "Type", out var typeElement)
                       && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;
            var message = TryGetPropertyIgnoreCase(errorElement, "Message", out var messageElement)
                          && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(type) || !string.IsNullOrWhiteSpace(message))
            {
                return $"{(string.IsNullOrWhiteSpace(type) ? "error" : type)}: {(string.IsNullOrWhiteSpace(message) ? "(none)" : message)}";
            }
        }

        return JsonSerializer.Serialize(errorElement, PrettyJsonOptions);
    }

    private static string TryFormatJsonString(string rawText)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawText);
            return JsonSerializer.Serialize(doc.RootElement, PrettyJsonOptions);
        }
        catch
        {
            return rawText;
        }
    }

    private static string GetBooleanText(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var element))
        {
            return "(none)";
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "(none)"
        };
    }

    private static string GetStringText(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return "(none)";
        }

        return ToCompactSingleLine(element.GetString(), 220);
    }

    private static bool TryGetNestedString(
        JsonElement root,
        string parentProperty,
        string childProperty,
        out string value)
    {
        value = string.Empty;
        if (!TryGetPropertyIgnoreCase(root, parentProperty, out var parent)
            || parent.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(parent, childProperty, out var child)
            || child.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = child.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetNestedInt32(
        JsonElement root,
        string parentProperty,
        string childProperty,
        out int value)
    {
        value = 0;
        if (!TryGetPropertyIgnoreCase(root, parentProperty, out var parent)
            || parent.ValueKind != JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(parent, childProperty, out var child)
            || child.ValueKind != JsonValueKind.Number
            || !child.TryGetInt32(out value))
        {
            return false;
        }

        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string ToCompactSingleLine(string? text, int maxLength)
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

    private sealed record PromptDelta(List<PromptDeltaMessage> Messages, int RemovedMessageCount);
    private sealed record PromptDeltaMessage(int MessageNumber, string Role, string Content, bool IsSuffixDelta);

    private bool ShouldFlushLiveSnapshot(string phase)
    {
        if (string.Equals(phase, "lead", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryParseEngineerStepObservation(phase, out _)
            || TryParseEngineerStepFailure(phase, out _);
    }

    private async Task PersistLiveSnapshotAsync()
    {
        if (!_captureEnabled || _runDir == null)
        {
            return;
        }

        await WriteJsonAsync("prompts.json", BuildSerializedPrompts());
        await WriteJsonAsync("outputs.json", _outputs);
        await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "transcript.md"), BuildTranscriptMarkdown());
    }

    private object BuildSerializedPrompts()
    {
        return new
        {
            phases = _prompts.ToDictionary(k => k.Key, v => v.Value.Select(m => new { role = m.Role.Value, content = m.Text }))
        };
    }

    private async Task WriteJsonAsync(string filename, object data)
    {
        if (_runDir == null) return;
        var path = Path.Combine(_runDir, filename);
        var options = new JsonSerializerOptions { WriteIndented = true };
        await _fileSystem.File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, options));
    }
}
