using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure.Services;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class AgenticTurnServiceTests
{
    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldReturnAggregatedObservation_WhenCommandsSucceed()
    {
        var containerServiceMock = new Mock<IContainerService>();
        var scriptExecution = new SandboxExecutionResult(0, "script ok", string.Empty, "script ok");
        var diffExecution = new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt", string.Empty, "diff --git a/a.txt b/a.txt");
        var changedFilesExecution = new SandboxExecutionResult(0, "a.txt\nb.txt", string.Empty, "a.txt\nb.txt");
        var buildExecution = new SandboxExecutionResult(0, "build ok", string.Empty, "build ok");

        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync("Console.WriteLine(\"ok\");"))
            .ReturnsAsync(scriptExecution);
        containerServiceMock.SetupSequence(x => x.ExecuteCommandAsync(It.IsAny<string>()))
            .ReturnsAsync(diffExecution)
            .ReturnsAsync(changedFilesExecution)
            .ReturnsAsync(buildExecution);

        var sut = new AgenticTurnService(containerServiceMock.Object);
        var settings = new AgenticTurnSettings();

        var observation = await sut.ExecuteAgenticTurnAsync("Console.WriteLine(\"ok\");", settings);

        Assert.Equal(scriptExecution, observation.ScriptExecution);
        Assert.Equal(diffExecution, observation.DiffExecution);
        Assert.Equal(changedFilesExecution, observation.ChangedFilesExecution);
        Assert.Equal(buildExecution, observation.BuildExecution);
        Assert.True(observation.HasChanges);
        Assert.True(observation.BuildPassed);
        Assert.Equal(new[] { "a.txt", "b.txt" }, observation.ChangedFiles);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.DiffCommand), Times.Once);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.ChangedFilesCommand), Times.Once);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.BuildCommand), Times.Once);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldParseChangedFiles_FromChangedFilesOutput()
    {
        var containerServiceMock = new Mock<IContainerService>();

        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
        containerServiceMock.SetupSequence(x => x.ExecuteCommandAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt", string.Empty, "diff --git a/a.txt b/a.txt"))
            .ReturnsAsync(new SandboxExecutionResult(0, "a.txt\r\n  b.txt  \r\n\r\nc.txt\r\nb.txt", string.Empty, "a.txt\r\n  b.txt  \r\n\r\nc.txt\r\nb.txt"))
            .ReturnsAsync(new SandboxExecutionResult(0, "build ok", string.Empty, "build ok"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("body", new AgenticTurnSettings());

        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, observation.ChangedFiles);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldSetHasChangesFalse_WhenDiffOutputEmpty()
    {
        var containerServiceMock = new Mock<IContainerService>();

        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
        containerServiceMock.SetupSequence(x => x.ExecuteCommandAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty))
            .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty))
            .ReturnsAsync(new SandboxExecutionResult(0, "build ok", string.Empty, "build ok"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("body", new AgenticTurnSettings());

        Assert.False(observation.HasChanges);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldSetBuildPassedFalse_WhenBuildExitCodeNonZero()
    {
        var containerServiceMock = new Mock<IContainerService>();

        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
        containerServiceMock.SetupSequence(x => x.ExecuteCommandAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt", string.Empty, "diff --git a/a.txt b/a.txt"))
            .ReturnsAsync(new SandboxExecutionResult(0, "a.txt", string.Empty, "a.txt"))
            .ReturnsAsync(new SandboxExecutionResult(1, "Build FAILED", string.Empty, "Build FAILED"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("body", new AgenticTurnSettings());

        Assert.False(observation.BuildPassed);
        Assert.Equal(1, observation.BuildExecution.ExitCode);
    }
}
