using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Infrastructure.Services;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class PromptOptRunEvaluatorTests
{
    [Fact]
    public async Task EvaluateAsync_WritesEvaluationArtifacts()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var gitRunnerMock = new Mock<IGitRunner>();
        var dotNetRunnerMock = new Mock<IDotNetRunner>();
        var evaluatorEngine = new EvaluatorEngine(new List<IEvaluatorRule>());

        var outDir = "/runs/out-1";
        fileSystem.Directory.CreateDirectory(outDir);

        gitRunnerMock.Setup(x => x.RunAsync("diff", It.IsAny<string?>()))
            .ReturnsAsync("diff content");
        dotNetRunnerMock.Setup(x => x.RunAsync("test", It.IsAny<string?>()))
            .ReturnsAsync("test passed");

        var sut = new PromptOptRunEvaluator(
            fileSystem,
            evaluatorEngine,
            gitRunnerMock.Object,
            dotNetRunnerMock.Object
        );

        // Act
        await sut.EvaluateAsync(outDir);

        // Assert
        Assert.True(fileSystem.File.Exists(Path.Combine(outDir, "git_diff.patch")));
        Assert.True(fileSystem.File.Exists(Path.Combine(outDir, "dotnet_test.txt")));
        Assert.True(fileSystem.File.Exists(Path.Combine(outDir, "evaluator.json")));
    }
}
