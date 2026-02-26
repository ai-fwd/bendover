using System;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.PromptOpt.CLI;
using Moq;
using Xunit;

namespace Bendover.PromptOpt.CLI.Tests;

public class BenchmarkRunOrchestratorTests
{
    private readonly Mock<IGitRunner> _gitRunnerMock;
    private readonly Mock<IAgentOrchestrator> _agentOrchestratorMock;
    private readonly Mock<IPromptBundleResolver> _bundleResolverMock;
    private readonly Mock<IPromptOptRunEvaluator> _runEvaluatorMock;
    private readonly MockFileSystem _fileSystem;
    private readonly IFileService _fileService;
    private readonly Mock<IPromptOptRunContextAccessor> _runContextAccessorMock;
    private readonly BenchmarkRunOrchestrator _sut;

    public BenchmarkRunOrchestratorTests()
    {
        _gitRunnerMock = new Mock<IGitRunner>();
        _agentOrchestratorMock = new Mock<IAgentOrchestrator>();
        _bundleResolverMock = new Mock<IPromptBundleResolver>();
        _runEvaluatorMock = new Mock<IPromptOptRunEvaluator>();
        _fileSystem = new MockFileSystem();
        _fileService = new FileService(_fileSystem);
        _runContextAccessorMock = new Mock<IPromptOptRunContextAccessor>();

        _sut = new BenchmarkRunOrchestrator(
            _gitRunnerMock.Object,
            _agentOrchestratorMock.Object,
            _bundleResolverMock.Object,
            _runEvaluatorMock.Object,
            _fileSystem,
            _runContextAccessorMock.Object,
            _fileService
        );
    }

    [Fact]
    public async Task RunAsync_ShouldUseBundlePathAndTaskPathAndEmitArtifacts()
    {
        // Arrange
        var bundlePath = "/bundle/bundle-456";
        var taskPath = "/task";
        var outputPath = "/out";
        var commitHash = "abc1234";
        var practicesPath = "/bundle/bundle-456/practices";
        var agentsPath = "/bundle/bundle-456/agents";
        var taskText = "Do the work";

        _fileSystem.AddFile(Path.Combine(taskPath, "base_commit.txt"), new MockFileData(commitHash));
        _fileSystem.AddFile(Path.Combine(taskPath, "task.md"), new MockFileData(taskText));
        _fileSystem.AddFile(
            Path.Combine(practicesPath, "tdd_spirit.md"),
            new MockFileData("---\nName: tdd_spirit\nTargetRole: Architect\nAreaOfConcern: Architecture\n---\ncontent"));
        _fileSystem.AddFile(Path.Combine(agentsPath, "lead.md"), new MockFileData("Lead prompt template"));
        _fileSystem.AddFile(Path.Combine(agentsPath, "engineer.md"), new MockFileData("Engineer prompt template"));
        _fileSystem.AddFile(
            Path.Combine(agentsPath, "tools.md"),
            new MockFileData("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract"));

        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("clone")), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("checkout")), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _bundleResolverMock.Setup(x => x.Resolve(bundlePath))
            .Returns(practicesPath);
        _agentOrchestratorMock.Setup(x => x.RunAsync(taskText, It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.RunAsync(bundlePath, taskPath, outputPath);

        // Assert
        _bundleResolverMock.Verify(x => x.Resolve(bundlePath), Times.Once);
        _agentOrchestratorMock.Verify(
            x => x.RunAsync(
                taskText,
                It.Is<IReadOnlyCollection<Practice>>(practices => practices.Any(p => p.Name == "tdd_spirit")),
                It.Is<string?>(path => path!.Replace('\\', '/') == ".bendover/promptopt/bundles/bundle-456/agents")),
            Times.Once);
        _runContextAccessorMock.VerifySet(
            x => x.Current = It.Is<PromptOptRunContext>(
                ctx => ctx.OutDir == outputPath
                       && ctx.Capture
                       && ctx.BundleId == "bundle-456"
                       && !ctx.ApplySandboxPatchToSource
            ),
            Times.Once);
        _runEvaluatorMock.Verify(x => x.EvaluateAsync(outputPath, bundlePath), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldResolveRelativeOutputPathToAbsolutePath()
    {
        var bundlePath = "/bundle/bundle-456";
        var taskPath = "/task";
        var outputPath = "relative/out";
        var expectedOutputPath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), outputPath));
        var practicesPath = "/bundle/bundle-456/practices";
        var agentsPath = "/bundle/bundle-456/agents";
        var taskText = "Do the work";

        _fileSystem.AddFile(Path.Combine(taskPath, "base_commit.txt"), new MockFileData("abc1234"));
        _fileSystem.AddFile(Path.Combine(taskPath, "task.md"), new MockFileData(taskText));
        _fileSystem.AddFile(
            Path.Combine(practicesPath, "tdd_spirit.md"),
            new MockFileData("---\nName: tdd_spirit\nTargetRole: Architect\nAreaOfConcern: Architecture\n---\ncontent"));
        _fileSystem.AddFile(Path.Combine(agentsPath, "lead.md"), new MockFileData("Lead prompt template"));
        _fileSystem.AddFile(Path.Combine(agentsPath, "engineer.md"), new MockFileData("Engineer prompt template"));
        _fileSystem.AddFile(
            Path.Combine(agentsPath, "tools.md"),
            new MockFileData("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract"));

        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("clone")), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("checkout")), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _bundleResolverMock.Setup(x => x.Resolve(bundlePath))
            .Returns(practicesPath);
        _agentOrchestratorMock.Setup(x => x.RunAsync(taskText, It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        await _sut.RunAsync(bundlePath, taskPath, outputPath);

        _runContextAccessorMock.VerifySet(
            x => x.Current = It.Is<PromptOptRunContext>(
                ctx => ctx.OutDir == expectedOutputPath
                       && ctx.Capture
                       && ctx.BundleId == "bundle-456"
                       && !ctx.ApplySandboxPatchToSource
            ),
            Times.Once);
        _runEvaluatorMock.Verify(x => x.EvaluateAsync(expectedOutputPath, bundlePath), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldCopyAgentsPromptsIntoWorkspaceBundleAgents()
    {
        var bundlePath = "/bundle/bundle-456";
        var taskPath = "/task";
        var outputPath = "/out";
        var practicesPath = "/bundle/bundle-456/practices";
        var agentsPath = "/bundle/bundle-456/agents";
        var taskText = "Do the work";
        var capturedWorkingDirectory = string.Empty;
        var sawCopiedLeadTemplate = false;
        var sawCopiedEngineerTemplate = false;
        var sawCopiedToolsTemplate = false;

        _fileSystem.AddFile(Path.Combine(taskPath, "base_commit.txt"), new MockFileData("abc1234"));
        _fileSystem.AddFile(Path.Combine(taskPath, "task.md"), new MockFileData(taskText));
        _fileSystem.AddFile(
            Path.Combine(practicesPath, "tdd_spirit.md"),
            new MockFileData("---\nName: tdd_spirit\nTargetRole: Architect\nAreaOfConcern: Architecture\n---\ncontent"));
        _fileSystem.AddFile(
            Path.Combine(agentsPath, "lead.md"),
            new MockFileData("Lead prompt template"));
        _fileSystem.AddFile(
            Path.Combine(agentsPath, "engineer.md"),
            new MockFileData("Engineer prompt template"));
        _fileSystem.AddFile(
            Path.Combine(agentsPath, "tools.md"),
            new MockFileData("# SDK Tool Usage Contract (Auto-generated)\n- sdk contract"));

        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("clone")), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("checkout")), It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((string _, string? workingDirectory, string? _) => capturedWorkingDirectory = workingDirectory ?? string.Empty)
            .ReturnsAsync("");
        _bundleResolverMock.Setup(x => x.Resolve(bundlePath))
            .Returns(practicesPath);
        _agentOrchestratorMock.Setup(x => x.RunAsync(taskText, It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
            .Callback(() =>
            {
                var paths = _fileSystem.AllFiles
                    .Select(x => x.Replace('\\', '/'))
                    .ToArray();
                sawCopiedLeadTemplate = paths.Any(path =>
                    path.EndsWith(".bendover/promptopt/bundles/bundle-456/agents/lead.md", StringComparison.Ordinal));
                sawCopiedEngineerTemplate = paths.Any(path =>
                    path.EndsWith(".bendover/promptopt/bundles/bundle-456/agents/engineer.md", StringComparison.Ordinal));
                sawCopiedToolsTemplate = paths.Any(path =>
                    path.EndsWith(".bendover/promptopt/bundles/bundle-456/agents/tools.md", StringComparison.Ordinal));
            })
            .Returns(Task.CompletedTask);

        await _sut.RunAsync(bundlePath, taskPath, outputPath);

        Assert.False(string.IsNullOrWhiteSpace(capturedWorkingDirectory));
        Assert.True(sawCopiedLeadTemplate);
        Assert.True(sawCopiedEngineerTemplate);
        Assert.True(sawCopiedToolsTemplate);
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenBundleAgentsDirectoryMissing()
    {
        var bundlePath = "/bundle/bundle-456";
        var taskPath = "/task";
        var outputPath = "/out";
        var practicesPath = "/bundle/bundle-456/practices";
        var taskText = "Do the work";

        _fileSystem.AddFile(Path.Combine(taskPath, "base_commit.txt"), new MockFileData("abc1234"));
        _fileSystem.AddFile(Path.Combine(taskPath, "task.md"), new MockFileData(taskText));
        _fileSystem.AddFile(
            Path.Combine(practicesPath, "tdd_spirit.md"),
            new MockFileData("---\nName: tdd_spirit\nTargetRole: Architect\nAreaOfConcern: Architecture\n---\ncontent"));

        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("clone")), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("checkout")), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _bundleResolverMock.Setup(x => x.Resolve(bundlePath))
            .Returns(practicesPath);

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _sut.RunAsync(bundlePath, taskPath, outputPath));
        Assert.Contains("Agents directory is required", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ShouldFail_WhenRequiredAgentFileMissing()
    {
        var bundlePath = "/bundle/bundle-456";
        var taskPath = "/task";
        var outputPath = "/out";
        var practicesPath = "/bundle/bundle-456/practices";
        var agentsPath = "/bundle/bundle-456/agents";
        var taskText = "Do the work";

        _fileSystem.AddFile(Path.Combine(taskPath, "base_commit.txt"), new MockFileData("abc1234"));
        _fileSystem.AddFile(Path.Combine(taskPath, "task.md"), new MockFileData(taskText));
        _fileSystem.AddFile(
            Path.Combine(practicesPath, "tdd_spirit.md"),
            new MockFileData("---\nName: tdd_spirit\nTargetRole: Architect\nAreaOfConcern: Architecture\n---\ncontent"));
        _fileSystem.AddFile(Path.Combine(agentsPath, "lead.md"), new MockFileData("Lead prompt template"));
        _fileSystem.AddFile(Path.Combine(agentsPath, "engineer.md"), new MockFileData("Engineer prompt template"));

        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("clone")), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _gitRunnerMock.Setup(x => x.RunAsync(It.Is<string>(s => s.StartsWith("checkout")), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync("");
        _bundleResolverMock.Setup(x => x.Resolve(bundlePath))
            .Returns(practicesPath);

        var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => _sut.RunAsync(bundlePath, taskPath, outputPath));
        Assert.Contains("tools.md", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
