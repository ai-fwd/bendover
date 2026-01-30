using System;
using System.Collections.Generic;
using Bendover.PromptOpt.CLI.Evaluation;
using Bendover.Application.Evaluation;
using Moq;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class EvaluatorEngineTests
{
    [Fact]
    public void Evaluate_ShouldAggregateRulesCorrectly()
    {
        // Arrange
        var context = new EvaluationContext("diff", "test output", new List<FileDiff>());
        
        var passingRule = new Mock<IEvaluatorRule>();
        passingRule.Setup(r => r.Evaluate(context))
            .Returns(new RuleResult("PassingRule", true, 0f, new[] { "Good job" }));

        var penaltyRule = new Mock<IEvaluatorRule>();
        penaltyRule.Setup(r => r.Evaluate(context))
            .Returns(new RuleResult("PenaltyRule", true, -0.2f, new[] { "Minor issue" }));

        var engine = new EvaluatorEngine(new[] { passingRule.Object, penaltyRule.Object });

        // Act
        var result = engine.Evaluate(context);

        // Assert
        Assert.True(result.Pass);
        Assert.Equal(0.8f, result.Score, 2); // 1.0 - 0.2
        Assert.Contains("Good job", result.Notes);
        Assert.Contains("Minor issue", result.Notes);
    }

    [Fact]
    public void Evaluate_HardFailure_ShouldFailAndZeroScore()
    {
        // Arrange
        var context = new EvaluationContext("diff", "test output", new List<FileDiff>());
        
        var hardFailRule = new Mock<IEvaluatorRule>();
        hardFailRule.Setup(r => r.Evaluate(context))
            .Returns(new RuleResult("HardFailRule", false, 0f, new[] { "Major failure" }, IsHardFailure: true));

        var engine = new EvaluatorEngine(new[] { hardFailRule.Object });

        // Act
        var result = engine.Evaluate(context);

        // Assert
        Assert.False(result.Pass);
        Assert.Equal(0.0f, result.Score);
        Assert.Contains("HardFailRule", result.Flags); // Maybe flags contain rule names that failed?
    }
    
    [Fact]
    public void Evaluate_ScoreShouldClampToZero()
    {
        // Arrange
        var context = new EvaluationContext("diff", "test output", new List<FileDiff>());
        
        var heavyPenalty = new Mock<IEvaluatorRule>();
        heavyPenalty.Setup(r => r.Evaluate(context))
            .Returns(new RuleResult("HeavyPenalty", true, -1.5f, Array.Empty<string>()));

        var engine = new EvaluatorEngine(new[] { heavyPenalty.Object });

        // Act
        var result = engine.Evaluate(context);

        // Assert
        Assert.True(result.Pass); // It passed hard gates
        Assert.Equal(0.0f, result.Score); // Clamped
    }
}
