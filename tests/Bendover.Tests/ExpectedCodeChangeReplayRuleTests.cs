using System;
using Bendover.Application.Evaluation;
using Bendover.PromptOpt.CLI.Evaluation.Rules;
using Xunit;

namespace Bendover.Tests;

public class ExpectedCodeChangeReplayRuleTests
{
    private readonly ExpectedCodeChangeReplayRule _sut = new();

    [Fact]
    public void Evaluate_ShouldHardFail_WhenPreviousRunRequiredChanges_AndDiffIsEmpty()
    {
        var context = BuildContext(diffContent: string.Empty, previousRunHadCodeChanges: true);

        var result = _sut.Evaluate(context);

        Assert.False(result.Passed);
        Assert.True(result.IsHardFailure);
    }

    [Fact]
    public void Evaluate_ShouldPass_WhenPreviousRunRequiredChanges_AndDiffExists()
    {
        var context = BuildContext(
            diffContent: "diff --git a/file.cs b/file.cs\n+line",
            previousRunHadCodeChanges: true);

        var result = _sut.Evaluate(context);

        Assert.True(result.Passed);
        Assert.False(result.IsHardFailure);
    }

    [Fact]
    public void Evaluate_ShouldPass_WhenPreviousRunDidNotRequireChanges_AndDiffIsEmpty()
    {
        var context = BuildContext(diffContent: string.Empty, previousRunHadCodeChanges: false);

        var result = _sut.Evaluate(context);

        Assert.True(result.Passed);
        Assert.False(result.IsHardFailure);
    }

    [Fact]
    public void Evaluate_ShouldPass_WhenPreviousRunExpectationIsMissing_AndDiffIsEmpty()
    {
        var context = BuildContext(diffContent: string.Empty, previousRunHadCodeChanges: null);

        var result = _sut.Evaluate(context);

        Assert.True(result.Passed);
        Assert.False(result.IsHardFailure);
    }

    private static EvaluationContext BuildContext(string diffContent, bool? previousRunHadCodeChanges)
    {
        return new EvaluationContext(
            DiffContent: diffContent,
            TestOutput: string.Empty,
            ChangedFiles: Array.Empty<FileDiff>(),
            PreviousRunHadCodeChanges: previousRunHadCodeChanges);
    }
}
