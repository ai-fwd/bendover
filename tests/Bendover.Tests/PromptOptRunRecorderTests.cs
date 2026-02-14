using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
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
    private readonly Mock<IPromptOptRunContextAccessor> _runContextAccessorMock;

    public PromptOptRunRecorderTests()
    {
        _fileSystem = new MockFileSystem();
        _runContextAccessorMock = new Mock<IPromptOptRunContextAccessor>();

        _sut = new PromptOptRunRecorder(
            _fileSystem,
            _runContextAccessorMock.Object
        );
    }

    [Fact]
    public async Task StartRunAsync_UsesHostOutDirAndWritesMeta_WhenCaptureEnabled()
    {
        // Arrange
        var outDir = "/runs/out-1";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true, RunId: "run-1"));

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
    public async Task FinalizeRunAsync_CaptureEnabled_WritesPromptsAndOutputs()
    {
        // Arrange
        var outDir = "/runs/out-2";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

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
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));
    }

    [Fact]
    public async Task FinalizeRunAsync_CaptureEnabled_WritesTranscriptWithPracticeDeliveryAudit()
    {
        // Arrange
        var outDir = "/runs/out-2-audit";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordOutputAsync("lead", "[\"codebase_overview\", \"tdd_spirit\"]");
        await _sut.RecordPromptAsync(
            "engineer",
            new List<ChatMessage>
            {
                new(ChatRole.System, "Header\n\nSelected Practices:\n- [codebase_overview] (Architecture): desc"),
                new(ChatRole.User, "Plan: Test Goal")
            });
        await _sut.RecordOutputAsync("engineer", "Console.WriteLine(\"hello\");");

        // Act
        await _sut.FinalizeRunAsync();

        // Assert
        var transcriptPath = Path.Combine(outDir, "transcript.md");
        Assert.True(_fileSystem.File.Exists(transcriptPath));
        var transcript = _fileSystem.File.ReadAllText(transcriptPath);

        Assert.Contains("## Practice Delivery Audit", transcript);
        Assert.Contains("| engineer | codebase_overview | yes |", transcript);
        Assert.Contains("| engineer | tdd_spirit | no |", transcript);
        Assert.Contains("### engineer", transcript);
        Assert.Contains("#### Prompt Message 1", transcript);
        Assert.Contains("#### Output", transcript);
    }

    [Fact]
    public async Task RecordArtifactAsync_CaptureEnabled_WritesArtifact()
    {
        var outDir = "/runs/out-artifacts";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordArtifactAsync("git_diff.patch", "diff content");
        await _sut.RecordArtifactAsync("dotnet_build.txt", "build content");
        await _sut.RecordArtifactAsync("dotnet_test_error.txt", "test failed");

        Assert.Equal("diff content", _fileSystem.File.ReadAllText(Path.Combine(outDir, "git_diff.patch")));
        Assert.Equal("build content", _fileSystem.File.ReadAllText(Path.Combine(outDir, "dotnet_build.txt")));
        Assert.Equal("test failed", _fileSystem.File.ReadAllText(Path.Combine(outDir, "dotnet_test_error.txt")));
    }

    [Fact]
    public async Task StartRunAsync_CaptureDisabled_DoesNotWriteMeta()
    {
        // Arrange
        var outDir = "/runs/out-3";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: false));

        // Act
        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");

        // Assert
        Assert.True(_fileSystem.Directory.Exists(outDir));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "goal.txt")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "base_commit.txt")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "bundle_id.txt")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "run_meta.json")));
    }

    [Fact]
    public async Task FinalizeRunAsync_CaptureDisabled_DoesNotWriteTranscript()
    {
        // Arrange
        var outDir = "/runs/out-4";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: false));

        // Act
        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("lead", new List<ChatMessage> { new(ChatRole.User, "Goal: Test Goal") });
        await _sut.RecordOutputAsync("lead", "[\"practice1\"]");
        await _sut.FinalizeRunAsync();

        // Assert
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));
    }
}
