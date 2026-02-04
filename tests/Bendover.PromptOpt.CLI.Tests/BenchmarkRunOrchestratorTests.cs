using System;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;
using Bendover.PromptOpt.CLI;
using Moq;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests;

public class BenchmarkRunOrchestratorTests
{
    private readonly Mock<IGitRunner> _gitRunnerMock;
    private readonly Mock<IAgentOrchestratorFactory> _agentOrchestratorFactoryMock;
    private readonly Mock<IAgentOrchestrator> _agentOrchestratorMock;
    private readonly Mock<IPromptBundleResolver> _bundleResolverMock;
    private readonly Mock<IDotNetRunner> _dotNetRunnerMock;
    private readonly MockFileSystem _fileSystem;
    private readonly Mock<IPromptOptRunContextAccessor> _runContextAccessorMock;
    private readonly BenchmarkRunOrchestrator _sut;

    public BenchmarkRunOrchestratorTests()
    {
        _gitRunnerMock = new Mock<IGitRunner>();
        _agentOrchestratorFactoryMock = new Mock<IAgentOrchestratorFactory>();
        _agentOrchestratorMock = new Mock<IAgentOrchestrator>();
        _bundleResolverMock = new Mock<IPromptBundleResolver>();
        _dotNetRunnerMock = new Mock<IDotNetRunner>();
        _fileSystem = new MockFileSystem();
        _runContextAccessorMock = new Mock<IPromptOptRunContextAccessor>();

        _sut = new BenchmarkRunOrchestrator(
            _gitRunnerMock.Object,
            _agentOrchestratorFactoryMock.Object,
            _bundleResolverMock.Object,
            _dotNetRunnerMock.Object,
            _fileSystem,
            _runContextAccessorMock.Object
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
        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("clone")), It.IsAny<string?>()))
            .ReturnsAsync("");

        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("checkout")), It.IsAny<string>())) // working dir might be temp
            .ReturnsAsync("");
        
        _bundleResolverMock.Setup(x => x.Resolve(bundlePath))
            .Returns(practicesPath);

        _agentOrchestratorFactoryMock.Setup(x => x.Create(practicesPath))
            .Returns(_agentOrchestratorMock.Object);

        _agentOrchestratorMock.Setup(x => x.RunAsync(taskText))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.RunAsync(bundlePath, taskPath, outputPath);

        // Assert
        // 1. Reads base_commit.txt (implicit by passing data to checkout)
        // 2. Runs git checkout
        _gitRunnerMock.Verify(x => x.RunAsync(It.Is<string>(s => s.StartsWith("checkout")), It.IsAny<string>()), Times.Once);

        // 3. Resolves practices
        _bundleResolverMock.Verify(x => x.Resolve(bundlePath), Times.Once);

        // 4. Invokes Agent
        _agentOrchestratorFactoryMock.Verify(x => x.Create(practicesPath), Times.Once);
        _agentOrchestratorMock.Verify(x => x.RunAsync(taskText), Times.Once);

        // 5. Sets run context and creates output directory
        Assert.True(_fileSystem.Directory.Exists(outputPath));
        _runContextAccessorMock.VerifySet(
            x => x.Current = It.Is<PromptOptRunContext>(
                ctx => ctx.OutDir == outputPath && ctx.Capture && ctx.Evaluate
            ),
            Times.Once);
    }
}
