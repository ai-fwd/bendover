using System.Text.Json;
using Bendover.Presentation.Console;

namespace Bendover.PromptOpt.CLI;

public sealed class PromptOptSummaryReader
{
    public PromptOptEvaluationSummary Read(string outDir, string bundleDirectory = "(pending)", bool includeLeadSummary = true)
    {
        var lead = includeLeadSummary ? ReadLead(outDir) : LeadReadResult.NotApplicable();
        var evaluator = ReadEvaluator(outDir);

        var state = ResolveState(includeLeadSummary, lead, evaluator);
        var errorMessage = lead.ErrorMessage ?? evaluator.ErrorMessage;

        return new PromptOptEvaluationSummary(
            State: state,
            BundleDirectory: bundleDirectory,
            OutputDirectory: outDir,
            LeadSelectedPractices: lead.SelectedPractices,
            Pass: evaluator.Pass,
            Score: evaluator.Score,
            EvaluatorSelectedPractices: evaluator.SelectedPractices,
            EvaluatorOffendingPractices: evaluator.OffendingPractices,
            ErrorMessage: errorMessage,
            IncludeLeadSummary: includeLeadSummary);
    }

    public string[] GetVerboseSummaryLines(string outDir, bool includeLeadSummary = true)
    {
        var lines = new List<string>
        {
            $"[promptopt] Out: {outDir}"
        };

        if (includeLeadSummary)
        {
            var lead = ReadLead(outDir);
            lines.Add(lead.VerboseLine);
        }

        var evaluator = ReadEvaluator(outDir);
        lines.AddRange(evaluator.VerboseLines);
        return lines.ToArray();
    }

    private static EvaluationPanelState ResolveState(bool includeLeadSummary, LeadReadResult lead, EvaluatorReadResult evaluator)
    {
        var leadState = includeLeadSummary ? lead.State : SummaryReadState.NotApplicable;

        if (leadState == SummaryReadState.ParseError || evaluator.State == SummaryReadState.ParseError)
        {
            return EvaluationPanelState.ParseError;
        }

        if (leadState == SummaryReadState.Missing || leadState == SummaryReadState.MissingData
            || evaluator.State == SummaryReadState.Missing || evaluator.State == SummaryReadState.MissingData)
        {
            return EvaluationPanelState.Missing;
        }

        return EvaluationPanelState.Completed;
    }

    private static LeadReadResult ReadLead(string outDir)
    {
        var outputsPath = Path.Combine(outDir, "outputs.json");
        if (!File.Exists(outputsPath))
        {
            return LeadReadResult.Missing(
                "[promptopt] outputs.json: missing",
                "outputs.json is missing.");
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(outputsPath));
            if (!doc.RootElement.TryGetProperty("lead", out var leadElement))
            {
                return LeadReadResult.MissingData(
                    "[promptopt] lead output: missing",
                    "lead output is missing from outputs.json.");
            }

            var selected = ExtractLeadSelections(leadElement);
            var selectedCsv = selected.Length == 0 ? "(none)" : string.Join(", ", selected);
            return LeadReadResult.Success(
                selected,
                $"[promptopt] lead.selected_practices: {selectedCsv}");
        }
        catch (Exception ex)
        {
            return LeadReadResult.ParseError(
                $"[promptopt] outputs.json parse error: {ex.Message}",
                $"outputs.json parse error: {ex.Message}");
        }
    }

    private static EvaluatorReadResult ReadEvaluator(string outDir)
    {
        var evaluatorPath = Path.Combine(outDir, "evaluator.json");
        if (!File.Exists(evaluatorPath))
        {
            return EvaluatorReadResult.Missing(
                "[promptopt] evaluator.json: missing",
                "evaluator.json is missing.");
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

            var lines = new List<string>
            {
                $"[promptopt] evaluator.pass={pass} score={score:0.###}"
            };

            if (!root.TryGetProperty("practice_attribution", out var practice)
                || practice.ValueKind != JsonValueKind.Object)
            {
                lines.Add("[promptopt] evaluator.practice_attribution: missing");
                return EvaluatorReadResult.MissingData(
                    pass,
                    score,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    lines,
                    "evaluator.practice_attribution is missing.");
            }

            var selected = ExtractStringArray(practice, "selected_practices");
            var offending = ExtractStringArray(practice, "offending_practices");
            lines.Add($"[promptopt] evaluator.selected_practices: {(selected.Length == 0 ? "(none)" : string.Join(", ", selected))}");
            lines.Add($"[promptopt] evaluator.offending_practices: {(offending.Length == 0 ? "(none)" : string.Join(", ", offending))}");
            return EvaluatorReadResult.Success(pass, score, selected, offending, lines);
        }
        catch (Exception ex)
        {
            return EvaluatorReadResult.ParseError(
                $"[promptopt] evaluator.json parse error: {ex.Message}",
                $"evaluator.json parse error: {ex.Message}");
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
            return Array.Empty<string>();
        }

        return Array.Empty<string>();
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

    private enum SummaryReadState
    {
        Success,
        Missing,
        MissingData,
        ParseError,
        NotApplicable
    }

    private sealed record LeadReadResult(
        SummaryReadState State,
        string[] SelectedPractices,
        string VerboseLine,
        string? ErrorMessage)
    {
        public static LeadReadResult Success(string[] selectedPractices, string verboseLine)
            => new(SummaryReadState.Success, selectedPractices, verboseLine, null);

        public static LeadReadResult Missing(string verboseLine, string errorMessage)
            => new(SummaryReadState.Missing, Array.Empty<string>(), verboseLine, errorMessage);

        public static LeadReadResult MissingData(string verboseLine, string errorMessage)
            => new(SummaryReadState.MissingData, Array.Empty<string>(), verboseLine, errorMessage);

        public static LeadReadResult ParseError(string verboseLine, string errorMessage)
            => new(SummaryReadState.ParseError, Array.Empty<string>(), verboseLine, errorMessage);

        public static LeadReadResult NotApplicable()
            => new(SummaryReadState.NotApplicable, Array.Empty<string>(), string.Empty, null);
    }

    private sealed record EvaluatorReadResult(
        SummaryReadState State,
        bool? Pass,
        double? Score,
        string[] SelectedPractices,
        string[] OffendingPractices,
        string[] VerboseLines,
        string? ErrorMessage)
    {
        public static EvaluatorReadResult Success(
            bool pass,
            double score,
            string[] selectedPractices,
            string[] offendingPractices,
            List<string> verboseLines)
            => new(SummaryReadState.Success, pass, score, selectedPractices, offendingPractices, verboseLines.ToArray(), null);

        public static EvaluatorReadResult Missing(string verboseLine, string errorMessage)
            => new(SummaryReadState.Missing, null, null, Array.Empty<string>(), Array.Empty<string>(), new[] { verboseLine }, errorMessage);

        public static EvaluatorReadResult MissingData(
            bool pass,
            double score,
            string[] selectedPractices,
            string[] offendingPractices,
            List<string> verboseLines,
            string errorMessage)
            => new(SummaryReadState.MissingData, pass, score, selectedPractices, offendingPractices, verboseLines.ToArray(), errorMessage);

        public static EvaluatorReadResult ParseError(string verboseLine, string errorMessage)
            => new(SummaryReadState.ParseError, null, null, Array.Empty<string>(), Array.Empty<string>(), new[] { verboseLine }, errorMessage);
    }
}
