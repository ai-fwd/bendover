using System;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using Bendover.Application;
using Moq;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests;

public class BenchmarkRunOrchestratorTests
{
    private readonly Mock<IGitRunner> _gitRunnerMock;
    private readonly Mock<IAgentRunner> _agentRunnerMock;
    private readonly Mock<IPromptBundleResolver> _bundleResolverMock;
    private readonly Mock<IDotNetRunner> _dotNetRunnerMock;
    private readonly MockFileSystem _fileSystem;
    private readonly BenchmarkRunOrchestrator _sut;

    public BenchmarkRunOrchestratorTests()
    {
        _gitRunnerMock = new Mock<IGitRunner>();
        _agentRunnerMock = new Mock<IAgentRunner>();
        _bundleResolverMock = new Mock<IPromptBundleResolver>();
        _dotNetRunnerMock = new Mock<IDotNetRunner>();
        _fileSystem = new MockFileSystem();

        _sut = new BenchmarkRunOrchestrator(
            _gitRunnerMock.Object,
            _agentRunnerMock.Object,
            _bundleResolverMock.Object,
            _dotNetRunnerMock.Object,
            _fileSystem
        );
    }

    [Fact]
    public async Task RunAsync_ShouldExecuteFullWorkflow()
    {
        // Arrange
        var bundlePath = "/bundle";
        var taskPath = "/task";
        var outputPath = "/out";
        var commitHash = "abc1234";
        var practicesPath = "/bundle/practices";
        var taskText = "Do the work";

        // Setup Task Files
        _fileSystem.AddFile(Path.Combine(taskPath, "base_commit.txt"), new MockFileData(commitHash));
        _fileSystem.AddFile(Path.Combine(taskPath, "task.md"), new MockFileData(taskText));

        // Setup Mocks
        _gitRunnerMock.Setup(x => x.CheckoutAsync(commitHash, It.IsAny<string>())) // working dir might be temp
            .Returns(Task.CompletedTask);
        
        _bundleResolverMock.Setup(x => x.Resolve(bundlePath))
            .Returns(practicesPath);

        _agentRunnerMock.Setup(x => x.RunAsync(It.IsAny<string>(), practicesPath, taskText))
            .ReturnsAsync(new AgentResult(true, "Agent Output", "agent_artifacts"));

        _gitRunnerMock.Setup(x => x.GetDiffAsync(It.IsAny<string>()))
            .ReturnsAsync("git diff content");

        _dotNetRunnerMock.Setup(x => x.RunTestsAsync(It.IsAny<string>()))
            .ReturnsAsync("Tests Passed");

        // Act
        await _sut.RunAsync(bundlePath, taskPath, outputPath);

        // Assert
        // 1. Reads base_commit.txt (implicit by passing data to checkout)
        // 2. Runs git checkout
        _gitRunnerMock.Verify(x => x.CheckoutAsync(commitHash, It.IsAny<string>()), Times.Once);

        // 3. Resolves practices
        _bundleResolverMock.Verify(x => x.Resolve(bundlePath), Times.Once);

        // 4. Invokes Agent
        _agentRunnerMock.Verify(x => x.RunAsync(It.IsAny<string>(), practicesPath, taskText), Times.Once);

        // 5. Writes artifacts
        Assert.True(_fileSystem.FileExists(Path.Combine(outputPath, "git_diff.patch")), "git_diff.patch should exist");
        Assert.Equal("git diff content", _fileSystem.GetFile(Path.Combine(outputPath, "git_diff.patch")).TextContents);

        Assert.True(_fileSystem.FileExists(Path.Combine(outputPath, "dotnet_test.txt")), "dotnet_test.txt should exist");
        Assert.Equal("Tests Passed", _fileSystem.GetFile(Path.Combine(outputPath, "dotnet_test.txt")).TextContents);

        Assert.True(_fileSystem.FileExists(Path.Combine(outputPath, "exit_code.txt")), "exit_code.txt should exist");
        Assert.Equal("0", _fileSystem.GetFile(Path.Combine(outputPath, "exit_code.txt")).TextContents);

        Assert.True(_fileSystem.FileExists(Path.Combine(outputPath, "run_meta.json")), "run_meta.json should exist");
        // Verify meta json content if needed, for now just existence
    }
}
