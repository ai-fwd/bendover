using Mystro.Presentation.Console;

namespace Mystro.PromptOpt.CLI;

public interface IPromptOptCliStatusSink
{
    void SetStatus(string message);
    void AddVerboseDetail(string message);
    void SetEvaluationState(EvaluationPanelState state);
    void SetEvaluationSummary(PromptOptEvaluationSummary summary);
}

public sealed class LivePromptOptCliStatusSink : IPromptOptCliStatusSink
{
    private readonly LiveCliDashboard _dashboard;
    private PromptOptEvaluationSummary _summary = PromptOptEvaluationSummary.Pending("(pending)");

    public LivePromptOptCliStatusSink(LiveCliDashboard dashboard)
    {
        _dashboard = dashboard;
    }

    public void SetStatus(string message)
    {
        _dashboard.SetLatestInfo(message);
    }

    public void AddVerboseDetail(string message)
    {
        _dashboard.SetLatestInfo(message);
    }

    public void SetEvaluationState(EvaluationPanelState state)
    {
        _summary = _summary with { State = state };
        _dashboard.UpdateEvaluation(_summary.ToPanelSnapshot());
    }

    public void SetEvaluationSummary(PromptOptEvaluationSummary summary)
    {
        _summary = summary;
        _dashboard.UpdateEvaluation(summary.ToPanelSnapshot());
    }
}

public sealed class PlainPromptOptCliStatusSink : IPromptOptCliStatusSink
{
    private readonly bool _verbose;

    public PlainPromptOptCliStatusSink(bool verbose)
    {
        _verbose = verbose;
    }

    public void SetStatus(string message)
    {
        WriteVerboseLine(message);
    }

    public void AddVerboseDetail(string message)
    {
        WriteVerboseLine(message);
    }

    public void SetEvaluationState(EvaluationPanelState state)
    {
    }

    public void SetEvaluationSummary(PromptOptEvaluationSummary summary)
    {
    }

    private void WriteVerboseLine(string message)
    {
        if (!_verbose)
        {
            return;
        }

        global::System.Console.WriteLine($"[promptopt][{DateTime.UtcNow:O}] {message}");
    }
}

public sealed class NoOpPromptOptCliStatusSink : IPromptOptCliStatusSink
{
    public static readonly NoOpPromptOptCliStatusSink Instance = new();

    private NoOpPromptOptCliStatusSink()
    {
    }

    public void SetStatus(string message)
    {
    }

    public void AddVerboseDetail(string message)
    {
    }

    public void SetEvaluationState(EvaluationPanelState state)
    {
    }

    public void SetEvaluationSummary(PromptOptEvaluationSummary summary)
    {
    }
}
