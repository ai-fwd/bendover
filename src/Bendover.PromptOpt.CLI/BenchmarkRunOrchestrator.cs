using System;
using System.IO;
using System.IO.Abstractions;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Interfaces;

namespace Bendover.PromptOpt.CLI;

public class BenchmarkRunOrchestrator
{
    private readonly IGitRunner _gitRunner;
    private readonly IAgentOrchestratorFactory _agentOrchestratorFactory;
    private readonly IPromptBundleResolver _bundleResolver;
    private readonly IPromptOptRunEvaluator _runEvaluator;
    private readonly IFileSystem _fileSystem;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;

    public BenchmarkRunOrchestrator(
        IGitRunner gitRunner,
        IAgentOrchestratorFactory agentOrchestratorFactory,
        IPromptBundleResolver bundleResolver,
        IPromptOptRunEvaluator runEvaluator,
        IFileSystem fileSystem,
        IPromptOptRunContextAccessor runContextAccessor)
    {
        _gitRunner = gitRunner;
        _agentOrchestratorFactory = agentOrchestratorFactory;
        _bundleResolver = bundleResolver;
        _runEvaluator = runEvaluator;
        _fileSystem = fileSystem;
        _runContextAccessor = runContextAccessor;
    }

    public Task RunAsync(string bundlePath, string taskPath, string outputPath)
    {
        return RunAsync(bundlePath, taskPath, outputPath, PromptOptRunOptions.GenerateAndScore);
    }

    public async Task RunAsync(string bundlePath, string taskPath, string outputPath, PromptOptRunOptions options)
    {
        if (options.Generate)
        {
            var workingDirectory = _fileSystem.Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _fileSystem.Directory.CreateDirectory(workingDirectory);
            Directory.CreateDirectory(workingDirectory);

            try
            {
                var commitPath = _fileSystem.Path.Combine(taskPath, "base_commit.txt");
                if (!_fileSystem.File.Exists(commitPath))
                {
                    throw new FileNotFoundException("base_commit.txt not found", commitPath);
                }

                var commitHash = await _fileSystem.File.ReadAllTextAsync(commitPath);
                commitHash = commitHash.Trim();

                await _gitRunner.RunAsync($"clone . \"{workingDirectory}\"");
                await _gitRunner.RunAsync($"checkout {commitHash}", workingDirectory);

                var practicesPath = _bundleResolver.Resolve(bundlePath);

                var taskFilePath = _fileSystem.Path.Combine(taskPath, "task.md");
                var taskText = await _fileSystem.File.ReadAllTextAsync(taskFilePath);

                if (!_fileSystem.Directory.Exists(outputPath))
                {
                    _fileSystem.Directory.CreateDirectory(outputPath);
                }

                _runContextAccessor.Current = new PromptOptRunContext(
                    outputPath,
                    Capture: true
                );

                var currentDir = Directory.GetCurrentDirectory();
                try
                {
                    Directory.SetCurrentDirectory(workingDirectory);
                    var agentOrchestrator = _agentOrchestratorFactory.Create(practicesPath);
                    await agentOrchestrator.RunAsync(taskText);
                }
                finally
                {
                    Directory.SetCurrentDirectory(currentDir);
                }
            }
            finally
            {
                // Cleanup if needed
            }
        }

        if (options.Score)
        {
            await _runEvaluator.EvaluateAsync(outputPath);
        }
    }
}

public record PromptOptRunOptions(bool Generate, bool Score)
{
    public static PromptOptRunOptions GenerateOnly => new(true, false);
    public static PromptOptRunOptions ScoreOnly => new(false, true);
    public static PromptOptRunOptions GenerateAndScore => new(true, true);
}
