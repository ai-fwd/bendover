using System.Collections.Generic;
using Bendover.PromptOpt.CLI.Evaluation;
using Bendover.PromptOpt.CLI.Evaluation.Rules;
using Bendover.Application.Evaluation;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class SingleImplInterfaceRuleTests
{
    private readonly CleanInterfacesRule _sut = new();

    private EvaluationContext MakeContext(string diffContent)
    {
        return new EvaluationContext(diffContent, string.Empty, new List<FileDiff>());
    }

    [Fact]
    public void Evaluate_NewInterfaceOneImpl_ShouldPenalty()
    {
        var diff = @"
+ public interface IMyThing { }
+ public class MyThing : IMyThing { }
";
        var result = _sut.Evaluate(MakeContext(diff));
        
        Assert.True(result.Passed);
        Assert.Equal(-0.1f, result.ScoreDelta); // Penalty for single impl
    }

    [Fact]
    public void Evaluate_NewInterfaceNoImpl_ShouldPenalty()
    {
        var diff = @"
+ public interface IOrphan { }
";
        var result = _sut.Evaluate(MakeContext(diff));
        
        Assert.True(result.Passed);
        Assert.Equal(-0.1f, result.ScoreDelta);
    }

    [Fact]
    public void Evaluate_NewInterfaceMultipleImpls_ShouldPassWithBonusOrZero()
    {
        var diff = @"
+ public interface IShared { }
+ public class ImplA : IShared { }
+ public class ImplB : IShared { }
";
        var result = _sut.Evaluate(MakeContext(diff));
        
        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }

    [Fact]
    public void Evaluate_NoNewInterface_ShouldPass()
    {
        var diff = @"
+ public class NothingSpecial { }
";
        var result = _sut.Evaluate(MakeContext(diff));
        
        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }
}
