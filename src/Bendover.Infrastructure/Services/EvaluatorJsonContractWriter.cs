using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bendover.Application.Evaluation;

namespace Bendover.Infrastructure.Services;

internal sealed class EvaluatorJsonContractWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public string Serialize(FinalEvaluation evaluation)
    {
        var attribution = evaluation.PracticeAttribution
            ?? new PracticeAttribution(Array.Empty<string>(), Array.Empty<string>(), new Dictionary<string, string[]>());

        var model = new EvaluatorContractDto
        {
            Pass = evaluation.Pass,
            Score = evaluation.Score,
            Flags = evaluation.Flags,
            Notes = evaluation.Notes,
            PracticeAttribution = new PracticeAttributionDto
            {
                SelectedPractices = attribution.SelectedPractices ?? Array.Empty<string>(),
                OffendingPractices = attribution.OffendingPractices ?? Array.Empty<string>(),
                NotesByPractice = attribution.NotesByPractice ?? new Dictionary<string, string[]>()
            }
        };

        return JsonSerializer.Serialize(model, Options);
    }

    private sealed class EvaluatorContractDto
    {
        [JsonPropertyName("pass")]
        public bool Pass { get; init; }

        [JsonPropertyName("score")]
        public float Score { get; init; }

        [JsonPropertyName("flags")]
        public string[] Flags { get; init; } = Array.Empty<string>();

        [JsonPropertyName("notes")]
        public string[] Notes { get; init; } = Array.Empty<string>();

        [JsonPropertyName("practice_attribution")]
        public PracticeAttributionDto PracticeAttribution { get; init; } = new();
    }

    private sealed class PracticeAttributionDto
    {
        [JsonPropertyName("selected_practices")]
        public string[] SelectedPractices { get; init; } = Array.Empty<string>();

        [JsonPropertyName("offending_practices")]
        public string[] OffendingPractices { get; init; } = Array.Empty<string>();

        [JsonPropertyName("notes_by_practice")]
        public Dictionary<string, string[]> NotesByPractice { get; init; } = new();
    }
}
