using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure.Services;
using Moq;

namespace Bendover.Tests;

public class AgenticTurnServiceTests
{
    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldSkipDiff_WhenScriptFails()
    {
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new ScriptExecutionResult(
                Execution: new SandboxExecutionResult(1, string.Empty, "script error", "script error"),
                CompletionSignaled: false,
                StepPlan: "Need to inspect file",
                ToolCall: "sdk.ReadFile(\"README.md\")"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.ReadFile(\"README.md\");", new AgenticTurnSettings());

        Assert.Equal(1, observation.ScriptExecution.ExitCode);
        Assert.Equal(-1, observation.DiffExecution.ExitCode);
        Assert.False(observation.CompletionSignaled);
        Assert.Equal("Need to inspect file", observation.StepPlan);
        Assert.Equal("sdk.ReadFile(\"README.md\")", observation.ToolCall);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldSkipDiff_ForNonDoneAction()
    {
        var settings = new AgenticTurnSettings(DiffCommand: "diff-cmd");
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new ScriptExecutionResult(
                Execution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                CompletionSignaled: false));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.Build();", settings);

        Assert.Equal(0, observation.ScriptExecution.ExitCode);
        Assert.Equal(-1, observation.DiffExecution.ExitCode);
        Assert.False(observation.HasChanges);
        Assert.False(observation.CompletionSignaled);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.DiffCommand), Times.Never);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldRunDiff_OnDoneAction()
    {
        var settings = new AgenticTurnSettings(DiffCommand: "diff-cmd");
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new ScriptExecutionResult(
                Execution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                CompletionSignaled: true));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.DiffCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt", string.Empty, "diff --git a/a.txt b/a.txt"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.Done();", settings);

        Assert.Equal(0, observation.ScriptExecution.ExitCode);
        Assert.Equal(0, observation.DiffExecution.ExitCode);
        Assert.True(observation.HasChanges);
        Assert.True(observation.CompletionSignaled);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.DiffCommand), Times.Once);
    }
}
