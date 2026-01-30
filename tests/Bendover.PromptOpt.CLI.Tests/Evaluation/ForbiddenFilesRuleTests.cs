using System.Collections.Generic;
using Bendover.PromptOpt.CLI.Evaluation;
using Bendover.PromptOpt.CLI.Evaluation.Rules;
using Bendover.Application.Evaluation;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests.Evaluation;

public class ForbiddenFilesRuleTests
{
    private readonly ForbiddenFilesRule _sut = new();

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
    public void Evaluate_GivenSafeFiles_ShouldPass()
    {
        var result = _sut.Evaluate(MakeContext("src/MyFile.cs", "tests/MyTests.cs"));
        
        Assert.True(result.Passed);
        Assert.False(result.IsHardFailure);
    }

    [Theory]
    [InlineData("bin/Debug/net10.0/App.dll")]
    [InlineData("src/Project/obj/Debug/temp.file")]
    [InlineData("data.lock")]
    [InlineData("Cargo.lock")]
    public void Evaluate_GivenForbiddenFile_ShouldFailHard(string path)
    {
        var result = _sut.Evaluate(MakeContext("src/Valid.cs", path));
        
        Assert.False(result.Passed);
        Assert.True(result.IsHardFailure);
        Assert.Contains(path, result.Notes[0]); // Should mention which file
    }
}
