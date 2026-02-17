using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure.Services;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class AgenticTurnServiceTests
{
    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldSkipFollowUpCommands_WhenScriptFails()
    {
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(1, string.Empty, "script error", "script error"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.File.Write(\"a.txt\", \"x\");", new AgenticTurnSettings());

        Assert.Equal(1, observation.ScriptExecution.ExitCode);
        Assert.Equal(-1, observation.DiffExecution.ExitCode);
        Assert.Equal(-1, observation.ChangedFilesExecution.ExitCode);
        Assert.Equal(-1, observation.BuildExecution.ExitCode);
        Assert.True(observation.IsMutationAction);
        Assert.False(observation.IsVerificationAction);
        Assert.Equal("mutation_write", observation.ActionKind);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldClassifyMutationWrite_AndSkipVerificationCommand()
    {
        var settings = new AgenticTurnSettings();
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.DiffCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt", string.Empty, "diff --git a/a.txt b/a.txt"));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.ChangedFilesCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "a.txt", string.Empty, "a.txt"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.File.Write(\"a.txt\", \"x\");", settings);

        Assert.Equal("mutation_write", observation.ActionKind);
        Assert.True(observation.IsMutationAction);
        Assert.False(observation.IsVerificationAction);
        Assert.True(observation.HasChanges);
        Assert.Equal(new[] { "a.txt" }, observation.ChangedFiles);
        Assert.False(observation.BuildPassed);
        Assert.Equal(-1, observation.BuildExecution.ExitCode);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.BuildCommand), Times.Never);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.TestCommand), Times.Never);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldRunBuild_ForVerificationBuildAction()
    {
        var settings = new AgenticTurnSettings(
            DiffCommand: "diff-cmd",
            ChangedFilesCommand: "changed-cmd",
            BuildCommand: "build-cmd");
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.DiffCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.ChangedFilesCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.BuildCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "build ok", string.Empty, "build ok"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.Run(\"dotnet build Bendover.sln\");", settings);

        Assert.Equal("verification_build", observation.ActionKind);
        Assert.True(observation.IsVerificationAction);
        Assert.False(observation.IsMutationAction);
        Assert.False(observation.HasChanges);
        Assert.True(observation.BuildPassed);
        Assert.Equal(0, observation.BuildExecution.ExitCode);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.BuildCommand), Times.Once);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.TestCommand), Times.Never);
    }

    [Fact]
    public async Task ExecuteAgenticTurnAsync_ShouldRunTest_ForVerificationTestAction()
    {
        var settings = new AgenticTurnSettings(
            DiffCommand: "diff-cmd",
            ChangedFilesCommand: "changed-cmd",
            TestCommand: "test-cmd");
        var containerServiceMock = new Mock<IContainerService>(MockBehavior.Strict);
        containerServiceMock.Setup(x => x.ExecuteScriptBodyAsync(It.IsAny<string>()))
            .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.DiffCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt", string.Empty, "diff --git a/a.txt b/a.txt"));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.ChangedFilesCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "a.txt", string.Empty, "a.txt"));
        containerServiceMock.Setup(x => x.ExecuteCommandAsync(settings.TestCommand))
            .ReturnsAsync(new SandboxExecutionResult(0, "test ok", string.Empty, "test ok"));

        var sut = new AgenticTurnService(containerServiceMock.Object);

        var observation = await sut.ExecuteAgenticTurnAsync("sdk.Shell.Execute(\"dotnet test\");", settings);

        Assert.Equal("verification_test", observation.ActionKind);
        Assert.True(observation.IsVerificationAction);
        Assert.False(observation.IsMutationAction);
        Assert.True(observation.HasChanges);
        Assert.True(observation.BuildPassed);
        Assert.Equal(0, observation.BuildExecution.ExitCode);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.TestCommand), Times.Once);
        containerServiceMock.Verify(x => x.ExecuteCommandAsync(settings.BuildCommand), Times.Never);
    }
}
