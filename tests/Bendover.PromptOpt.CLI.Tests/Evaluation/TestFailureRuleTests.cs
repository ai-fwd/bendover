using System.Collections.Generic;
using Bendover.PromptOpt.CLI.Evaluation;
using Bendover.PromptOpt.CLI.Evaluation.Rules;
using Bendover.Application.Evaluation;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class TestFailureRuleTests
{
    private readonly TestFailureRule _sut = new();

    private EvaluationContext MakeContext(string testOutput)
    {
        return new EvaluationContext(string.Empty, testOutput, new List<FileDiff>());
    }

    [Fact]
    public void Evaluate_GivenCleanSuccess_ShouldPass()
    {
        var output = "Test summary: total: 3, failed: 0, succeeded: 3, skipped: 0, duration: 1.1s";
        var result = _sut.Evaluate(MakeContext(output));
        
        Assert.True(result.Passed);
        Assert.False(result.IsHardFailure);
        Assert.Equal(0f, result.ScoreDelta);
    }

    [Fact]
    public void Evaluate_GivenFailedTests_ShouldFailHard()
    {
        var output = "Test summary: total: 3, failed: 1, succeeded: 2, skipped: 0";
        var result = _sut.Evaluate(MakeContext(output));
        
        Assert.False(result.Passed);
        Assert.True(result.IsHardFailure);
    }

    [Fact]
    public void Evaluate_GivenBuildFailed_ShouldFailHard()
    {
        var output = "Build FAILED.";
        var result = _sut.Evaluate(MakeContext(output));
        
        Assert.False(result.Passed);
        Assert.True(result.IsHardFailure);
    }

    [Fact]
    public void Evaluate_GivenCSharpError_ShouldFailHard()
    {
        var output = "error CS0246: The type or namespace name 'Foo' could not be found";
        var result = _sut.Evaluate(MakeContext(output));
        
        Assert.False(result.Passed);
        Assert.True(result.IsHardFailure);
    }

    [Fact]
    public void Evaluate_GivenAmbiguousOutput_ShouldFailHardWithNote()
    {
        var output = "Some random text that doesn't look like tests";
        var result = _sut.Evaluate(MakeContext(output));
        
        Assert.False(result.Passed);
        Assert.True(result.IsHardFailure);
        Assert.Contains("Unable to determine test result", result.Notes);
    }
}
