using System.IO.Abstractions.TestingHelpers;
using Bendover.Application.Interfaces;
using Bendover.PromptOpt.CLI;
using Moq;

namespace Bendover.PromptOpt.CLI.Tests;

public class RunScoringOrchestratorTests : IDisposable
{
    private readonly string _originalCurrentDirectory;
    private readonly DirectoryInfo _repoRootDirectory;
    private readonly MockFileSystem _fileSystem;
    private readonly Mock<IPromptOptRunEvaluator> _runEvaluatorMock;
    private readonly RunScoringOrchestrator _sut;

    public RunScoringOrchestratorTests()
    {
        _originalCurrentDirectory = Directory.GetCurrentDirectory();
        _repoRootDirectory = Directory.CreateTempSubdirectory("run-score-tests-");
        Directory.SetCurrentDirectory(_repoRootDirectory.FullName);

        _fileSystem = new MockFileSystem();
        _runEvaluatorMock = new Mock<IPromptOptRunEvaluator>();
        _runEvaluatorMock.Setup(x => x.EvaluateAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        _sut = new RunScoringOrchestrator(_fileSystem, _runEvaluatorMock.Object);
    }

    [Fact]
    public async Task ScoreAsync_UsesCurrentRootBundle_WhenBundleIdIsCurrent()
    {
        var runDirectory = SeedRun("run-1", "current");
        SeedCurrentPractices();

        var outDirectory = await _sut.ScoreAsync("run-1", bundleOverridePath: null);

        Assert.Equal(runDirectory, outDirectory);
        _runEvaluatorMock.Verify(
            x => x.EvaluateAsync(
                runDirectory,
                Path.Combine(_repoRootDirectory.FullName, ".bendover")),
            Times.Once);
    }

    [Fact]
    public async Task ScoreAsync_UsesCurrentRootBundle_WhenBundleIdIsDefaultAlias()
    {
        var runDirectory = SeedRun("run-2", "default");
        SeedCurrentPractices();

        var outDirectory = await _sut.ScoreAsync("run-2", bundleOverridePath: null);

        Assert.Equal(runDirectory, outDirectory);
        _runEvaluatorMock.Verify(
            x => x.EvaluateAsync(
                runDirectory,
                Path.Combine(_repoRootDirectory.FullName, ".bendover")),
            Times.Once);
    }

    [Fact]
    public async Task ScoreAsync_UsesBundleOverride_WhenProvided()
    {
        var runDirectory = SeedRun("run-3", "current");
        SeedCurrentPractices();

        var overrideBundle = Path.Combine(_repoRootDirectory.FullName, "custom_bundle");
        _fileSystem.AddFile(Path.Combine(overrideBundle, "practices", "custom.md"), new MockFileData("content"));

        await _sut.ScoreAsync("run-3", bundleOverridePath: overrideBundle);

        _runEvaluatorMock.Verify(x => x.EvaluateAsync(runDirectory, overrideBundle), Times.Once);
    }

    [Fact]
    public async Task ScoreAsync_UsesRunBundleId_WhenSpecificBundleIsRecorded()
    {
        var runDirectory = SeedRun("run-4", "gengepa_12345678");
        var resolvedBundlePath = Path.Combine(
            _repoRootDirectory.FullName,
            ".bendover",
            "promptopt",
            "bundles",
            "gengepa_12345678");
        _fileSystem.AddFile(Path.Combine(resolvedBundlePath, "practices", "p.md"), new MockFileData("content"));

        await _sut.ScoreAsync("run-4", bundleOverridePath: null);

        _runEvaluatorMock.Verify(x => x.EvaluateAsync(runDirectory, resolvedBundlePath), Times.Once);
    }

    [Fact]
    public async Task ScoreAsync_Throws_WhenResolvedBundlePathMissing()
    {
        SeedRun("run-6", "missing_bundle");

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => _sut.ScoreAsync("run-6", bundleOverridePath: null));

        Assert.Contains("Resolved bundle path does not exist", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private string SeedRun(string runId, string bundleId)
    {
        var runDirectory = Path.Combine(
            _repoRootDirectory.FullName,
            ".bendover",
            "promptopt",
            "runs",
            runId);
        _fileSystem.AddFile(Path.Combine(runDirectory, "bundle_id.txt"), new MockFileData(bundleId));
        _fileSystem.AddFile(Path.Combine(runDirectory, "outputs.json"), new MockFileData("{\"lead\":\"[\\\"tdd_spirit\\\"]\"}"));
        _fileSystem.AddFile(Path.Combine(runDirectory, "git_diff.patch"), new MockFileData("diff --git a/a.cs b/a.cs\n+line"));
        _fileSystem.AddFile(Path.Combine(runDirectory, "dotnet_test.txt"), new MockFileData("Test run passed"));
        return runDirectory;
    }

    private void SeedCurrentPractices()
    {
        var currentPracticePath = Path.Combine(_repoRootDirectory.FullName, ".bendover", "practices", "tdd_spirit.md");
        _fileSystem.AddFile(currentPracticePath, new MockFileData("---\nName: tdd_spirit\n---\n\ncontent"));
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCurrentDirectory);
        try
        {
            _repoRootDirectory.Delete(recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
