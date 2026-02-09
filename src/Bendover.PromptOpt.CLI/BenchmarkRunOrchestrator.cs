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

            var practicesPath = _bundleResolver.Resolve(bundlePath);
            Log(verbose, $"Resolved practices path: {practicesPath}");
            var practiceService = new PracticeService(_fileService, practicesPath);
            var practices = (await practiceService.GetPracticesAsync()).ToList();
            Log(verbose, $"Loaded {practices.Count} practices.");
            var bundleId = _fileSystem.Path.GetFileName(bundlePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

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
                BundleId: bundleId
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
            // Cleanup if needed
        }

        Log(verbose, "Running evaluation...");
        await _runEvaluator.EvaluateAsync(outputPath, bundlePath);
        Log(verbose, "Evaluation completed.");
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
