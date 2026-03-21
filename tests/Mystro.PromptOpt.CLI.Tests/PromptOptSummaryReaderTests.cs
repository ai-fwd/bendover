using Mystro.Presentation.Console;

namespace Mystro.PromptOpt.CLI.Tests;

public class PromptOptSummaryReaderTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly PromptOptSummaryReader _sut = new();

    public PromptOptSummaryReaderTests()
    {
        _tempDirectory = Directory.CreateTempSubdirectory("promptopt-summary-tests-").FullName;
    }

    [Fact]
    public void Read_ReturnsCompletedSummary_WhenOutputsAndEvaluatorArePresent()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "outputs.json"), "{\"lead\":\"[\\\"coding_style\\\",\\\"tests\\\"]\"}");
        File.WriteAllText(
            Path.Combine(_tempDirectory, "evaluator.json"),
            """
            {
              "pass": true,
              "score": 0.7,
              "practice_attribution": {
                "selected_practices": ["coding_style"],
                "offending_practices": ["tests"]
              }
            }
            """);

        var summary = _sut.Read(_tempDirectory);

        Assert.Equal(EvaluationPanelState.Completed, summary.State);
        Assert.Equal(["coding_style", "tests"], summary.LeadSelectedPractices);
        Assert.True(summary.Pass);
        Assert.Equal(0.7, summary.Score);
        Assert.Equal(["coding_style"], summary.EvaluatorSelectedPractices);
        Assert.Equal(["tests"], summary.EvaluatorOffendingPractices);
        Assert.Null(summary.ErrorMessage);
    }

    [Fact]
    public void Read_ReturnsMissing_WhenOutputsJsonIsMissing()
    {
        File.WriteAllText(
            Path.Combine(_tempDirectory, "evaluator.json"),
            """
            {
              "pass": true,
              "score": 1.0,
              "practice_attribution": {
                "selected_practices": [],
                "offending_practices": []
              }
            }
            """);

        var summary = _sut.Read(_tempDirectory);

        Assert.Equal(EvaluationPanelState.Missing, summary.State);
        Assert.Contains("outputs.json", summary.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Read_IgnoresLeadSummary_WhenRequested()
    {
        File.WriteAllText(
            Path.Combine(_tempDirectory, "evaluator.json"),
            """
            {
              "pass": false,
              "score": 0.2,
              "practice_attribution": {
                "selected_practices": ["coding_style"],
                "offending_practices": ["coding_style"]
              }
            }
            """);

        var summary = _sut.Read(_tempDirectory, includeLeadSummary: false);

        Assert.Equal(EvaluationPanelState.Completed, summary.State);
        Assert.False(summary.IncludeLeadSummary);
        Assert.Equal(["coding_style"], summary.EvaluatorSelectedPractices);
    }

    [Fact]
    public void GetVerboseSummaryLines_EmitsExpectedPlainSummary()
    {
        File.WriteAllText(Path.Combine(_tempDirectory, "outputs.json"), "{\"lead\":\"[\\\"coding_style\\\"]\"}");
        File.WriteAllText(
            Path.Combine(_tempDirectory, "evaluator.json"),
            """
            {
              "pass": true,
              "score": 0.7,
              "practice_attribution": {
                "selected_practices": ["coding_style"],
                "offending_practices": []
              }
            }
            """);

        var lines = _sut.GetVerboseSummaryLines(_tempDirectory);

        Assert.Contains($"[promptopt] Out: {_tempDirectory}", lines);
        Assert.Contains("[promptopt] lead.selected_practices: coding_style", lines);
        Assert.Contains("[promptopt] evaluator.pass=True score=0.7", lines);
        Assert.Contains("[promptopt] evaluator.selected_practices: coding_style", lines);
        Assert.Contains("[promptopt] evaluator.offending_practices: (none)", lines);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
