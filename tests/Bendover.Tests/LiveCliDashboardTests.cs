using System.Reflection;
using Bendover.Domain.Interfaces;
using Bendover.Presentation.Console;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Bendover.Tests;

public class LiveCliDashboardTests
{
    [Fact]
    public void BuildLayout_IncludesEvaluationPanel_WhenEnabled()
    {
        var dashboard = new LiveCliDashboard();
        dashboard.Initialize(new LiveCliDashboardOptions(
            ModelSummary: "endpoint mode",
            RunId: "run-1",
            RunDir: ".bendover/promptopt/runs/run-1",
            Goal: "Ship the feature",
            ShowEvaluationPanel: true));
        dashboard.UpdateEvaluation(new EvaluationPanelSnapshot(
            State: EvaluationPanelState.Completed,
            OutputDirectory: ".bendover/promptopt/runs/run-1",
            LeadSelectedPracticesText: "coding_style",
            EvaluatorPassScoreText: "pass=True score=0.7",
            EvaluatorSelectedPracticesText: "coding_style",
            EvaluatorOffendingPracticesText: "(none)",
            ErrorMessage: null));
        dashboard.AddStep(new AgentStepEvent(
            StepNumber: 1,
            Plan: "Inspect the code",
            Tool: "exec",
            Observation: "Observed",
            IsCompletion: false));

        var text = RenderLayout(dashboard);

        Assert.Contains("Evaluation", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pass=True score=0.7", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Step #1", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildLayout_HidesEvaluationPanel_WhenDisabled()
    {
        var dashboard = new LiveCliDashboard();
        dashboard.Initialize(new LiveCliDashboardOptions(
            ModelSummary: "endpoint mode",
            RunId: "run-2",
            RunDir: ".bendover/promptopt/runs/run-2",
            Goal: "Ship the feature",
            ShowEvaluationPanel: false));

        var text = RenderLayout(dashboard);

        Assert.DoesNotContain("Evaluation", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Execution", text, StringComparison.OrdinalIgnoreCase);
    }

    private static string RenderLayout(LiveCliDashboard dashboard)
    {
        var buildLayout = typeof(LiveCliDashboard).GetMethod(
            "BuildLayout",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(buildLayout);
        var renderable = Assert.IsAssignableFrom<IRenderable>(buildLayout!.Invoke(dashboard, Array.Empty<object>()));

        AnsiConsole.Record();
        AnsiConsole.Write(renderable);
        return AnsiConsole.ExportText();
    }
}
