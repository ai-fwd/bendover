using System.Collections.Generic;
using Bendover.Application.Evaluation;
using Bendover.PromptOpt.CLI.Evaluation.Rules;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class CodingStyleRuleTests
{
    private readonly CodingStyleRule _sut = new();

    [Fact]
    public void Evaluate_WhenDiffContainsChainedStringEqualsMembership_ShouldSoftFail()
    {
        var diff = BuildCSharpPatch(
            "+if (string.Equals(name, \".git\", StringComparison.OrdinalIgnoreCase)",
            "+    || string.Equals(name, \"tmp\", StringComparison.OrdinalIgnoreCase))",
            "+{",
            "+    continue;",
            "+}");

        var result = _sut.Evaluate(MakeContext(diff));

        Assert.False(result.Passed);
        Assert.False(result.IsHardFailure);
        Assert.Equal(-0.1f, result.ScoreDelta);
        Assert.Equal(nameof(CodingStyleRule), result.RuleName);
        Assert.Contains("HashSet<T>", string.Join(" ", result.Notes));
    }

    [Fact]
    public void Evaluate_WhenDiffContainsChainedDoubleEqualsMembership_ShouldSoftFail()
    {
        var diff = BuildCSharpPatch(
            "+if (fileName == \"script_body.csx\" || fileName == \"script_result.json\")",
            "+{",
            "+    continue;",
            "+}");

        var result = _sut.Evaluate(MakeContext(diff));

        Assert.False(result.Passed);
        Assert.False(result.IsHardFailure);
        Assert.Equal(-0.1f, result.ScoreDelta);
        Assert.NotEmpty(result.Notes);
    }

    [Fact]
    public void Evaluate_WhenDiffUsesHashSetContainsPattern_ShouldPass()
    {
        var diff = BuildCSharpPatch(
            "+var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)",
            "+{",
            "+    \".git\",",
            "+    \"tmp\",",
            "+};",
            "+",
            "+if (excludedDirs.Contains(name)) continue;");

        var result = _sut.Evaluate(MakeContext(diff));

        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Evaluate_WhenChainUsesDifferentOperands_ShouldPass()
    {
        var diff = BuildCSharpPatch(
            "+if (dirName == \".git\" || fileName == \"tmp\")",
            "+{",
            "+    continue;",
            "+}");

        var result = _sut.Evaluate(MakeContext(diff));

        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }

    [Fact]
    public void Evaluate_WhenOnlyRemovedLinesContainViolation_ShouldPass()
    {
        var diff = BuildCSharpPatch(
            "-if (fileName == \"script_body.csx\" || fileName == \"script_result.json\")",
            "-{",
            "-    continue;",
            "-}",
            "+if (excludedFiles.Contains(fileName))",
            "+{",
            "+    continue;",
            "+}");

        var result = _sut.Evaluate(MakeContext(diff));

        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }

    [Fact]
    public void Evaluate_WhenDiffIsEmpty_ShouldPass()
    {
        var result = _sut.Evaluate(MakeContext(string.Empty));

        Assert.True(result.Passed);
        Assert.Equal(0f, result.ScoreDelta);
    }

    private static EvaluationContext MakeContext(string diffContent)
    {
        return new EvaluationContext(diffContent, string.Empty, new List<FileDiff>());
    }

    private static string BuildCSharpPatch(params string[] hunkLines)
    {
        return
            $"diff --git a/src/Sample.cs b/src/Sample.cs\n" +
            $"index 1111111..2222222 100644\n" +
            $"--- a/src/Sample.cs\n" +
            $"+++ b/src/Sample.cs\n" +
            $"@@ -1,1 +1,1 @@\n" +
            $"{string.Join("\n", hunkLines)}\n";
    }
}
