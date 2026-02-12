using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;
using IOAbstractions = System.IO.Abstractions;

namespace Bendover.PromptOpt.CLI;

public class BenchmarkRunOrchestrator
{
    private readonly IGitRunner _gitRunner;
    private readonly IAgentOrchestrator _agentOrchestrator;
    private readonly IPromptBundleResolver _bundleResolver;
    private readonly IPromptOptRunEvaluator _runEvaluator;
    private readonly IOAbstractions.IFileSystem _fileSystem;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;
    private readonly IFileService _fileService;

    public BenchmarkRunOrchestrator(
        IGitRunner gitRunner,
        IAgentOrchestrator agentOrchestrator,
        IPromptBundleResolver bundleResolver,
        IPromptOptRunEvaluator runEvaluator,
        IOAbstractions.IFileSystem fileSystem,
        IPromptOptRunContextAccessor runContextAccessor,
        IFileService fileService)
    {
        _gitRunner = gitRunner;
        _agentOrchestrator = agentOrchestrator;
        _bundleResolver = bundleResolver;
        _runEvaluator = runEvaluator;
        _fileSystem = fileSystem;
        _runContextAccessor = runContextAccessor;
        _fileService = fileService;
    }

    public async Task RunAsync(string bundlePath, string taskPath, string outputPath)
    {
        await RunAsync(bundlePath, taskPath, outputPath, verbose: false);
    }

    public async Task RunAsync(string bundlePath, string taskPath, string outputPath, bool verbose)
    {
        Log(verbose, $"Starting run. bundle={bundlePath} task={taskPath} out={outputPath}");
        var workingDirectory = _fileSystem.Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(workingDirectory);
        Log(verbose, $"Working directory: {workingDirectory}");

        try
        {
            var commitPath = _fileSystem.Path.Combine(taskPath, "base_commit.txt");
            if (!_fileSystem.File.Exists(commitPath))
            {
                throw new FileNotFoundException("base_commit.txt not found", commitPath);
            }

            var commitHash = await _fileSystem.File.ReadAllTextAsync(commitPath);
            commitHash = commitHash.Trim();
            Log(verbose, $"Using base commit: {commitHash}");

            Log(verbose, "Cloning repository...");
            await _gitRunner.RunAsync($"clone . \"{workingDirectory}\"");
            Log(verbose, "Clone completed.");
            Log(verbose, $"Checking out commit {commitHash}...");
            await _gitRunner.RunAsync($"checkout {commitHash}", workingDirectory);
            Log(verbose, "Checkout completed.");

            var bundleId = _fileSystem.Path.GetFileName(bundlePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var sourcePracticesPath = _bundleResolver.Resolve(bundlePath);
            Log(verbose, $"Resolved source practices path: {sourcePracticesPath}");
            var sourceBundleRoot = _fileSystem.Path.GetDirectoryName(sourcePracticesPath)
                ?? throw new InvalidOperationException($"Could not resolve bundle root from practices path: {sourcePracticesPath}");
            var sourceAgentsPath = _fileSystem.Path.Combine(sourceBundleRoot, "agents");

            var bundlePracticesRootRelativePath = _fileSystem.Path.Combine(".bendover", "promptopt", "bundles", bundleId, "practices");
            var workspacePracticesPath = _fileSystem.Path.Combine(workingDirectory, bundlePracticesRootRelativePath);
            CopyDirectory(sourcePracticesPath, workspacePracticesPath);
            Log(verbose, $"Copied practices into workspace: {workspacePracticesPath}");

            if (_fileSystem.Directory.Exists(sourceAgentsPath))
            {
                var bundleAgentsRootRelativePath = _fileSystem.Path.Combine(".bendover", "promptopt", "bundles", bundleId, "agents");
                var workspaceAgentsPath = _fileSystem.Path.Combine(workingDirectory, bundleAgentsRootRelativePath);
                CopyDirectory(sourceAgentsPath, workspaceAgentsPath);
                Log(verbose, $"Copied agents into workspace: {workspaceAgentsPath}");
            }
            else
            {
                Log(verbose, $"Agents directory not found in bundle (optional): {sourceAgentsPath}");
            }

            var practiceService = new PracticeService(_fileService, workspacePracticesPath);
            var practices = (await practiceService.GetPracticesAsync()).ToList();
            Log(verbose, $"Loaded {practices.Count} practices.");

            var taskFilePath = _fileSystem.Path.Combine(taskPath, "task.md");
            var taskText = await _fileSystem.File.ReadAllTextAsync(taskFilePath);
            Log(verbose, $"Loaded task file: {taskFilePath}");

            if (!_fileSystem.Directory.Exists(outputPath))
            {
                _fileSystem.Directory.CreateDirectory(outputPath);
                Log(verbose, $"Created output directory: {outputPath}");
            }
            else
            {
                Log(verbose, $"Using existing output directory: {outputPath}");
            }

            _runContextAccessor.Current = new PromptOptRunContext(
                outputPath,
                Capture: true,
                BundleId: bundleId,
                ApplySandboxPatchToSource: false,
                PracticesRootRelativePath: bundlePracticesRootRelativePath
            );
            Log(verbose, $"Set run context. bundleId={bundleId}");

            var currentDir = Directory.GetCurrentDirectory();
            try
            {
                Log(verbose, $"Switching current directory: {currentDir} -> {workingDirectory}");
                Directory.SetCurrentDirectory(workingDirectory);
                Log(verbose, "Running agent orchestrator...");
                await _agentOrchestrator.RunAsync(taskText, practices);
                Log(verbose, "Agent orchestrator completed.");
            }
            finally
            {
                Directory.SetCurrentDirectory(currentDir);
                Log(verbose, $"Restored current directory: {currentDir}");
            }
        }
        finally
        {
            try
            {
                if (_fileSystem.Directory.Exists(workingDirectory))
                {
                    _fileSystem.Directory.Delete(workingDirectory, recursive: true);
                    Log(verbose, $"Cleaned up working directory: {workingDirectory}");
                }
            }
            catch (Exception ex)
            {
                Log(verbose, $"Failed to clean working directory '{workingDirectory}': {ex.Message}");
            }
        }

        Log(verbose, "Running evaluation...");
        await _runEvaluator.EvaluateAsync(outputPath, bundlePath);
        Log(verbose, "Evaluation completed.");
    }

    private void CopyDirectory(string sourcePath, string targetPath)
    {
        if (!_fileSystem.Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourcePath}");
        }

        _fileSystem.Directory.CreateDirectory(targetPath);

        var directories = _fileSystem.Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories);
        foreach (var directory in directories)
        {
            var relative = _fileSystem.Path.GetRelativePath(sourcePath, directory);
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.Combine(targetPath, relative));
        }

        var files = _fileSystem.Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relative = _fileSystem.Path.GetRelativePath(sourcePath, file);
            var destination = _fileSystem.Path.Combine(targetPath, relative);
            var destinationDirectory = _fileSystem.Path.GetDirectoryName(destination);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                _fileSystem.Directory.CreateDirectory(destinationDirectory);
            }

            _fileSystem.File.Copy(file, destination, overwrite: true);
        }
    }

    private static void Log(bool verbose, string message)
    {
        if (!verbose)
        {
            return;
        }

        Console.WriteLine($"[promptopt][{DateTime.UtcNow:O}] {message}");
    }
}
