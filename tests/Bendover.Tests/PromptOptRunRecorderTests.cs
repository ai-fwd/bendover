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
    private readonly EvaluatorEngine _evaluatorEngine;

    public PromptOptRunRecorderTests()
    {
        _fileSystem = new MockFileSystem();
        _gitRunnerMock = new Mock<IGitRunner>();
        _dotNetRunnerMock = new Mock<IDotNetRunner>();
        _evaluatorEngine = new EvaluatorEngine(new List<IEvaluatorRule>()); // No rules for simple test

        _sut = new PromptOptRunRecorder(
            _fileSystem,
            _evaluatorEngine,
            _gitRunnerMock.Object,
            _dotNetRunnerMock.Object
        );
    }

    [Fact]
    public async Task StartRunAsync_CreatesRunDirectoryAndFiles()
    {
        // Act
        var runId = await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");

        // Assert
        var expectedPath = Path.Combine(".bendover", "promptopt", "runs", runId);
        Assert.True(_fileSystem.Directory.Exists(expectedPath));
        Assert.True(_fileSystem.File.Exists(Path.Combine(expectedPath, "goal.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(expectedPath, "base_commit.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(expectedPath, "bundle_id.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(expectedPath, "run_meta.json")));

        Assert.Equal("Test Goal", _fileSystem.File.ReadAllText(Path.Combine(expectedPath, "goal.txt")));
    }

    [Fact]
    public async Task FinalizeRunAsync_WritesAllArtifacts()
    {
        // Arrange
        var runId = await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("lead", new List<ChatMessage> { new ChatMessage(ChatRole.User, "Goal: Test Goal") });
        await _sut.RecordOutputAsync("lead", "[\"practice1\"]"); // Simulating serialized output
        await _sut.RecordPromptAsync("architect", new List<ChatMessage> { new ChatMessage(ChatRole.User, "prompt") });
        await _sut.RecordOutputAsync("architect", "plan details");

        _gitRunnerMock.Setup(x => x.RunAsync("diff")).ReturnsAsync("diff content");
        _dotNetRunnerMock.Setup(x => x.RunAsync("test")).ReturnsAsync("test passed");

        // Act
        await _sut.FinalizeRunAsync();

        // Assert
        var runDir = Path.Combine(".bendover", "promptopt", "runs", runId);
        Assert.True(_fileSystem.File.Exists(Path.Combine(runDir, "prompts.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(runDir, "outputs.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(runDir, "git_diff.patch")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(runDir, "dotnet_test.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(runDir, "evaluator.json")));

        var diffContent = _fileSystem.File.ReadAllText(Path.Combine(runDir, "git_diff.patch"));
        Assert.Equal("diff content", diffContent);
    }
}
