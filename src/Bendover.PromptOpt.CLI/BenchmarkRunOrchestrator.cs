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
    private readonly IAgentRunner _agentRunner;
    private readonly IPromptBundleResolver _bundleResolver;
    private readonly IDotNetRunner _dotNetRunner;
    private readonly IFileSystem _fileSystem;

    public BenchmarkRunOrchestrator(
        IGitRunner gitRunner,
        IAgentRunner agentRunner,
        IPromptBundleResolver bundleResolver,
        IDotNetRunner dotNetRunner,
        IFileSystem fileSystem)
    {
        _gitRunner = gitRunner;
        _agentRunner = agentRunner;
        _bundleResolver = bundleResolver;
        _dotNetRunner = dotNetRunner;
        _fileSystem = fileSystem;
    }

    public async Task RunAsync(string bundlePath, string taskPath, string outputPath)
    {
        var workingDirectory = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);

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

            var agentResult = await _agentRunner.RunAsync(workingDirectory, practicesPath, taskText);

            var diff = await _gitRunner.RunAsync("diff", workingDirectory);
            
            var testOutput = await _dotNetRunner.RunAsync("test", workingDirectory);

            if (!_fileSystem.Directory.Exists(outputPath))
            {
                _fileSystem.Directory.CreateDirectory(outputPath);
            }

            await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(outputPath, "git_diff.patch"), diff);
            await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(outputPath, "dotnet_test.txt"), testOutput);
            await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(outputPath, "exit_code.txt"), agentResult.Success ? "0" : "1");
            await _fileSystem.File.WriteAllTextAsync(_fileSystem.Path.Combine(outputPath, "run_meta.json"), "{}");
        }
        finally
        {
             // Cleanup if needed
        }
    }
}
