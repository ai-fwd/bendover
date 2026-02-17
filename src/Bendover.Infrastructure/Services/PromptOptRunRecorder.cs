using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
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
        // Store byte-for-byte exact messages
        // But we need to convert them to a serializable format or just store the list
        _prompts[phase] = new List<ChatMessage>(messages);
        return Task.CompletedTask;
    }

    public Task RecordOutputAsync(string phase, string output)
    {
        _outputs[phase] = output;
        return Task.CompletedTask;
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
        if (_runDir == null) return;

        if (_captureEnabled)
        {
            var serializedPrompts = new
            {
                phases = _prompts.ToDictionary(k => k.Key, v => v.Value.Select(m => new { role = m.Role.Value, content = m.Text }))
            };

            await WriteJsonAsync("prompts.json", serializedPrompts);

            // Write outputs.json
            await WriteJsonAsync("outputs.json", _outputs);
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "transcript.md"), BuildTranscriptMarkdown());

        }

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
        foreach (var phase in phaseNames)
        {
            lines.Add($"### {phase}");
            lines.Add(string.Empty);

            if (_prompts.TryGetValue(phase, out var messages))
            {
                for (var i = 0; i < messages.Count; i++)
                {
                    var message = messages[i];
                    lines.Add($"#### Prompt Message {i + 1}");
                    lines.Add($"role: {message.Role.Value}");
                    lines.Add(string.Empty);
                    AppendCodeBlock(lines, message.Text ?? string.Empty);
                    lines.Add(string.Empty);
                }
            }

            if (_outputs.TryGetValue(phase, out var output))
            {
                lines.Add("#### Output");
                lines.Add(string.Empty);
                AppendCodeBlock(lines, output);
                lines.Add(string.Empty);
            }
        }

        return string.Join('\n', lines);
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
            || TryParseEngineerStepFailure(phase, out _)
            || TryParseLegacyObservationAttempt(phase, out _))
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

        if (TryParseLegacyObservationAttempt(phase, out var legacyObservationAttempt))
        {
            return legacyObservationAttempt;
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

        if (TryParseEngineerStepObservation(phase, out _)
            || TryParseLegacyObservationAttempt(phase, out _))
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

    private static bool TryParseLegacyObservationAttempt(string phase, out int attempt)
    {
        if (string.Equals(phase, "agentic_turn_observation", StringComparison.OrdinalIgnoreCase))
        {
            attempt = 0;
            return true;
        }

        const string prefix = "agentic_turn_observation_retry_";
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

    private async Task WriteJsonAsync(string filename, object data)
    {
        if (_runDir == null) return;
        var path = Path.Combine(_runDir, filename);
        var options = new JsonSerializerOptions { WriteIndented = true };
        await _fileSystem.File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, options));
    }
}
