using Mystro.Presentation.Console;

namespace Mystro.PromptOpt.CLI;

public sealed record PromptOptEvaluationSummary(
    EvaluationPanelState State,
    string BundleDirectory,
    string OutputDirectory,
    string[] LeadSelectedPractices,
    bool? Pass,
    double? Score,
    string[] EvaluatorSelectedPractices,
    string[] EvaluatorOffendingPractices,
    string? ErrorMessage,
    bool IncludeLeadSummary = true)
{
    public static PromptOptEvaluationSummary Pending(string outputDirectory, string bundleDirectory = "(pending)", bool includeLeadSummary = true)
    {
        return new PromptOptEvaluationSummary(
            State: EvaluationPanelState.Pending,
            BundleDirectory: bundleDirectory,
            OutputDirectory: outputDirectory,
            LeadSelectedPractices: Array.Empty<string>(),
            Pass: null,
            Score: null,
            EvaluatorSelectedPractices: Array.Empty<string>(),
            EvaluatorOffendingPractices: Array.Empty<string>(),
            ErrorMessage: null,
            IncludeLeadSummary: includeLeadSummary);
    }

    public static PromptOptEvaluationSummary Failed(
        string outputDirectory,
        string errorMessage,
        string bundleDirectory = "(pending)",
        bool includeLeadSummary = true)
    {
        return new PromptOptEvaluationSummary(
            State: EvaluationPanelState.Failed,
            BundleDirectory: bundleDirectory,
            OutputDirectory: outputDirectory,
            LeadSelectedPractices: Array.Empty<string>(),
            Pass: null,
            Score: null,
            EvaluatorSelectedPractices: Array.Empty<string>(),
            EvaluatorOffendingPractices: Array.Empty<string>(),
            ErrorMessage: errorMessage,
            IncludeLeadSummary: includeLeadSummary);
    }

    public EvaluationPanelSnapshot ToPanelSnapshot()
    {
        return new EvaluationPanelSnapshot(
            State: State,
            BundleDirectory: string.IsNullOrWhiteSpace(BundleDirectory) ? "(pending)" : BundleDirectory,
            OutputDirectory: string.IsNullOrWhiteSpace(OutputDirectory) ? "(pending)" : OutputDirectory,
            LeadSelectedPracticesText: IncludeLeadSummary
                ? FormatArray(LeadSelectedPractices, State)
                : "(not available)",
            EvaluatorPassScoreText: FormatPassScore(),
            EvaluatorSelectedPracticesText: FormatArray(EvaluatorSelectedPractices, State),
            EvaluatorOffendingPracticesText: FormatArray(EvaluatorOffendingPractices, State),
            ErrorMessage: ErrorMessage);
    }

    private string FormatPassScore()
    {
        if (Pass.HasValue || Score.HasValue)
        {
            var passText = Pass switch
            {
                true => "✅",
                false => "❌",
                _ => "(unknown)"
            };
            var scoreText = Score.HasValue ? Score.Value.ToString("0.###") : "(unknown)";
            return $"{passText} / {scoreText}";
        }

        return State switch
        {
            EvaluationPanelState.Pending => "(pending)",
            EvaluationPanelState.Running => "(pending)",
            EvaluationPanelState.Missing => "(missing)",
            EvaluationPanelState.ParseError => "(parse error)",
            EvaluationPanelState.Failed => "(failed)",
            _ => "(none)"
        };
    }

    private static string FormatArray(string[] values, EvaluationPanelState state)
    {
        if (values.Length > 0)
        {
            return string.Join(", ", values);
        }

        return state switch
        {
            EvaluationPanelState.Pending => "(pending)",
            EvaluationPanelState.Running => "(pending)",
            EvaluationPanelState.Missing => "(missing)",
            EvaluationPanelState.ParseError => "(parse error)",
            EvaluationPanelState.Failed => "(failed)",
            _ => "(none)"
        };
    }
}
