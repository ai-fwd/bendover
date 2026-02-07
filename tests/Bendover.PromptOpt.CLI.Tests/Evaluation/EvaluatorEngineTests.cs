using System;
using System.Collections.Generic;
using Bendover.Application.Evaluation;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class EvaluatorEngineTests
{
    [Fact]
    public void Evaluate_ShouldAggregateRulesCorrectly()
    {
        // Arrange
        var context = new EvaluationContext("diff", "test output", new List<FileDiff>());
        
        var passingRule = new FixedRule("PassingRule", true, 0f, new[] { "Good job" });
        var penaltyRule = new FixedRule("PenaltyRule", true, -0.2f, new[] { "Minor issue" });

        var engine = new EvaluatorEngine(new[] { passingRule, penaltyRule });

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
        
        var hardFailRule = new FixedRule("HardFailRule", false, 0f, new[] { "Major failure" }, isHardFailure: true);

        var engine = new EvaluatorEngine(new[] { hardFailRule });

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
        
        var heavyPenalty = new FixedRule("HeavyPenalty", true, -1.5f, Array.Empty<string>());

        var engine = new EvaluatorEngine(new[] { heavyPenalty });

        // Act
        var result = engine.Evaluate(context);

        // Assert
        Assert.True(result.Pass); // It passed hard gates
        Assert.Equal(0.0f, result.Score); // Clamped
    }

    [Fact]
    public void Evaluate_PracticeBoundRule_Runs_WhenMatchingPracticeIsSelected()
    {
        var rule = new TddSpiritRule();
        var context = new EvaluationContext(
            "diff",
            "test output",
            new List<FileDiff>(),
            SelectedPractices: new[] { "tdd_spirit" },
            AllPractices: new[] { "tdd_spirit", "readme_hygiene" });

        var engine = new EvaluatorEngine(new IEvaluatorRule[] { rule });

        engine.Evaluate(context);

        Assert.Equal(1, rule.Calls);
    }

    [Fact]
    public void Evaluate_PracticeBoundRule_DoesNotRun_WhenMatchingPracticeIsNotSelected()
    {
        var rule = new ReadmeHygieneRule();
        var context = new EvaluationContext(
            "diff",
            "test output",
            new List<FileDiff>(),
            SelectedPractices: new[] { "tdd_spirit" },
            AllPractices: new[] { "tdd_spirit", "readme_hygiene" });

        var engine = new EvaluatorEngine(new IEvaluatorRule[] { rule });

        engine.Evaluate(context);

        Assert.Equal(0, rule.Calls);
    }

    [Fact]
    public void Evaluate_GlobalRule_Runs_Always_WhenNoPracticeMatches()
    {
        var rule = new GlobalSafetyRule();
        var context = new EvaluationContext(
            "diff",
            "test output",
            new List<FileDiff>(),
            SelectedPractices: Array.Empty<string>(),
            AllPractices: new[] { "tdd_spirit", "readme_hygiene" });

        var engine = new EvaluatorEngine(new IEvaluatorRule[] { rule });

        engine.Evaluate(context);

        Assert.Equal(1, rule.Calls);
    }

    private sealed class FixedRule : IEvaluatorRule
    {
        private readonly RuleResult _result;

        public FixedRule(string name, bool passed, float scoreDelta, string[] notes, bool isHardFailure = false)
        {
            _result = new RuleResult(name, passed, scoreDelta, notes, IsHardFailure: isHardFailure);
        }

        public RuleResult Evaluate(EvaluationContext context) => _result;
    }

    private sealed class TddSpiritRule : IEvaluatorRule
    {
        public int Calls { get; private set; }

        public RuleResult Evaluate(EvaluationContext context)
        {
            Calls++;
            return new RuleResult(nameof(TddSpiritRule), true, 0f, Array.Empty<string>());
        }
    }

    private sealed class ReadmeHygieneRule : IEvaluatorRule
    {
        public int Calls { get; private set; }

        public RuleResult Evaluate(EvaluationContext context)
        {
            Calls++;
            return new RuleResult(nameof(ReadmeHygieneRule), true, 0f, Array.Empty<string>());
        }
    }

    private sealed class GlobalSafetyRule : IEvaluatorRule
    {
        public int Calls { get; private set; }

        public RuleResult Evaluate(EvaluationContext context)
        {
            Calls++;
            return new RuleResult(nameof(GlobalSafetyRule), true, 0f, Array.Empty<string>());
        }
    }
}
