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

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.ReadFile(\"README.md\");");

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
        const string expectedDiffCommand = "cd /workspace && git diff";
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new ScriptExecutionResult(
                Execution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                CompletionSignaled: false));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.Build();");

        Assert.Equal(0, observation.ScriptExecution.ExitCode);
        Assert.Equal(-1, observation.DiffExecution.ExitCode);
        Assert.False(observation.HasChanges);
        Assert.False(observation.CompletionSignaled);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(expectedDiffCommand), Times.Never);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldRunDiff_OnDoneAction()
    {
        const string expectedDiffCommand = "cd /workspace && git diff";
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new ScriptExecutionResult(
                Execution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                CompletionSignaled: true));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(expectedDiffCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt", string.Empty, "diff --git a/a.txt b/a.txt"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.Done();");

        Assert.Equal(0, observation.ScriptExecution.ExitCode);
        Assert.Equal(0, observation.DiffExecution.ExitCode);
        Assert.True(observation.HasChanges);
        Assert.True(observation.CompletionSignaled);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(expectedDiffCommand), Times.Once);
    }
}
