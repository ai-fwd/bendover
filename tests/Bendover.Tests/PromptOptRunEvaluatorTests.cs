using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Infrastructure.Services;
using Xunit;

namespace Bendover.Tests;

public class PromptOptRunEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_LoadsArtifactsAndWritesEvaluation()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var capturingRule = new CapturingRule();
        var evaluatorEngine = new EvaluatorEngine(new[] { capturingRule });

        var outDir = "/runs/out-1";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(Path.Combine(outDir, "git_diff.patch"), "diff content");
        fileSystem.File.WriteAllText(Path.Combine(outDir, "dotnet_test.txt"), "test passed");

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);

        // Act
        await sut.EvaluateAsync(outDir);

        // Assert
        Assert.True(fileSystem.File.Exists(Path.Combine(outDir, "evaluator.json")));
        Assert.Equal("diff content", capturingRule.LastContext?.DiffContent);
        Assert.Equal("test passed", capturingRule.LastContext?.TestOutput);
    }

    private sealed class CapturingRule : IEvaluatorRule
    {
        public EvaluationContext? LastContext { get; private set; }

        public RuleResult Evaluate(EvaluationContext context)
        {
            LastContext = context;
            return new RuleResult("capture", true, 0, new string[0]);
        }
    }
}
