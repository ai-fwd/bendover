using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Infrastructure.Services;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class PromptOptRunRecorderTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly PromptOptRunRecorder _sut;
    private readonly Mock<IGitRunner> _gitRunnerMock;
    private readonly Mock<IDotNetRunner> _dotNetRunnerMock;
    private readonly Mock<IPromptOptRunContextAccessor> _runContextAccessorMock;
    private readonly EvaluatorEngine _evaluatorEngine;

    public PromptOptRunRecorderTests()
    {
        _fileSystem = new MockFileSystem();
        _gitRunnerMock = new Mock<IGitRunner>();
        _dotNetRunnerMock = new Mock<IDotNetRunner>();
        _runContextAccessorMock = new Mock<IPromptOptRunContextAccessor>();
        _evaluatorEngine = new EvaluatorEngine(new List<IEvaluatorRule>()); // No rules for simple test

        _sut = new PromptOptRunRecorder(
            _fileSystem,
            _evaluatorEngine,
            _gitRunnerMock.Object,
            _dotNetRunnerMock.Object,
            _runContextAccessorMock.Object
        );
    }

    [Fact]
    public async Task StartRunAsync_UsesHostOutDirAndWritesMeta_WhenCaptureEnabled()
    {
        // Arrange
        var outDir = "/runs/out-1";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true, Evaluate: false, RunId: "run-1"));

        // Act
        var runId = await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");

        // Assert
        Assert.Equal("run-1", runId);
        Assert.True(_fileSystem.Directory.Exists(outDir));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "goal.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "base_commit.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "bundle_id.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "run_meta.json")));

        Assert.Equal("Test Goal", _fileSystem.File.ReadAllText(Path.Combine(outDir, "goal.txt")));
    }

    [Fact]
    public async Task FinalizeRunAsync_CaptureEnabledAndEvaluateDisabled_WritesPromptsAndOutputsOnly()
    {
        // Arrange
        var outDir = "/runs/out-2";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true, Evaluate: false));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("lead", new List<ChatMessage> { new ChatMessage(ChatRole.User, "Goal: Test Goal") });
        await _sut.RecordOutputAsync("lead", "[\"practice1\"]"); // Simulating serialized output
        await _sut.RecordPromptAsync("architect", new List<ChatMessage> { new ChatMessage(ChatRole.User, "prompt") });
        await _sut.RecordOutputAsync("architect", "plan details");

        // Act
        await _sut.FinalizeRunAsync();

        // Assert
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "git_diff.patch")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "dotnet_test.txt")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "evaluator.json")));

        _gitRunnerMock.Verify(x => x.RunAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        _dotNetRunnerMock.Verify(x => x.RunAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task FinalizeRunAsync_CaptureDisabledAndEvaluateEnabled_WritesEvaluationArtifactsOnly()
    {
        // Arrange
        var outDir = "/runs/out-3";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: false, Evaluate: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");

        _gitRunnerMock.Setup(x => x.RunAsync("diff", It.IsAny<string?>())).ReturnsAsync("diff content");
        _dotNetRunnerMock.Setup(x => x.RunAsync("test", It.IsAny<string?>())).ReturnsAsync("test passed");

        // Act
        await _sut.FinalizeRunAsync();

        // Assert
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "git_diff.patch")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "dotnet_test.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "evaluator.json")));
    }
}
