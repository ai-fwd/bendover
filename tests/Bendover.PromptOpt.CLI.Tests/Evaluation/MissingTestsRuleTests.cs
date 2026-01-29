using System.Collections.Generic;
using Bendover.PromptOpt.CLI.Evaluation;
using Bendover.PromptOpt.CLI.Evaluation.Rules;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class MissingTestsRuleTests
{
    private readonly MissingTestsRule _sut = new();

    private EvaluationContext MakeContext(params string[] changedFiles)
    {
        var diffs = new List<FileDiff>();
        foreach(var f in changedFiles)
        {
            diffs.Add(new FileDiff(f, FileStatus.Modified, ""));
        }
        return new EvaluationContext(string.Empty, string.Empty, diffs);
    }

    [Fact]
    public void Evaluate_ProdChangedAndTestChanged_ShouldPass()
    {
        var result = _sut.Evaluate(MakeContext("src/Prod.cs", "tests/ProdTests.cs"));
        
        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }

    [Fact]
    public void Evaluate_ProdChangedNoTests_ShouldFailSoft()
    {
        var result = _sut.Evaluate(MakeContext("src/Prod.cs"));
        
        // Pass=True because soft rule, but ScoreDelta negative
        Assert.True(result.Passed); 
        Assert.Equal(-0.2f, result.ScoreDelta);
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public void Evaluate_OnlyDocsChanged_ShouldPass()
    {
        var result = _sut.Evaluate(MakeContext("src/README.md", "src/docs/Design.txt"));
        
        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }
    
    [Fact]
    public void Evaluate_OnlyTestsChanged_ShouldPass()
    {
        var result = _sut.Evaluate(MakeContext("tests/CleanupTests.cs"));
        
        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }
}
