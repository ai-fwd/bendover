using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Transcript;

public sealed class EventTranscriptWriter : ITranscriptWriter
{
    private const int TranscriptPreviewLimit = 320;

    private readonly IAgentEventPublisher _events;
    private IReadOnlyCollection<string> _selectedPractices = Array.Empty<string>();

    public EventTranscriptWriter(IAgentEventPublisher events)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public Task WriteSelectedPracticesAsync(IReadOnlyCollection<string> selectedPractices)
    {
        _selectedPractices = selectedPractices ?? Array.Empty<string>();
        var selectedCsv = string.Join(", ", _selectedPractices.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return _events.TranscriptAsync(new AgentTranscriptEvent(
            Category: "run",
            Phase: string.Empty,
            Message: $"[transcript][run] selected_practices={selectedCsv}"));
    }

    public async Task WritePromptAsync(string phase, IReadOnlyList<ChatMessage> messages)
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

        await _events.TranscriptAsync(new AgentTranscriptEvent(
            Category: "prompt",
            Phase: phase,
            Message: $"[transcript][prompt] phase={phase} roles={roles} user={userSummary} system_selected_practices={deliveredCsv}"));

        foreach (var practice in _selectedPractices
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var delivered = deliveredPractices.Contains(practice) ? "yes" : "no";
            await _events.TranscriptAsync(new AgentTranscriptEvent(
                Category: "audit",
                Phase: phase,
                Message: $"[transcript][audit] phase={phase} practice={practice} delivered={delivered}"));
        }
    }

    public Task WriteOutputAsync(string phase, string output)
    {
        var preview = ToCompactSingleLine(output);
        return _events.TranscriptAsync(new AgentTranscriptEvent(
            Category: "output",
            Phase: phase,
            Message: $"[transcript][output] phase={phase} chars={output.Length} preview={preview}"));
    }

    public Task WriteFailureAsync(string phase, string failureDigest)
    {
        return _events.TranscriptAsync(new AgentTranscriptEvent(
            Category: "failure",
            Phase: phase,
            Message: $"[transcript][failure] phase={phase}\n{failureDigest}"));
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
}
