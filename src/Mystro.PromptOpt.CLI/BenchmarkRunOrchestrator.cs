using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mystro.Application;
using Mystro.Application.Interfaces;
using Mystro.Domain.Interfaces;
using IOAbstractions = System.IO.Abstractions;

namespace Mystro.PromptOpt.CLI;

public class BenchmarkRunOrchestrator
{
    private static readonly string[] RequiredAgentPromptFiles =
    {
        "lead.md",
        "engineer.md",
        "tools.md"
    };

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
        await RunAsync(bundlePath, taskPath, outputPath, verbose: false, statusSink: null);
    }

    public async Task RunAsync(
        string bundlePath,
        string taskPath,
        string outputPath,
        bool verbose,
        IPromptOptCliStatusSink? statusSink)
    {
        var sink = statusSink ?? NoOpPromptOptCliStatusSink.Instance;
        var invocationDirectory = Directory.GetCurrentDirectory();
        var resolvedOutputPath = Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.GetFullPath(Path.Combine(invocationDirectory, outputPath));

        sink.SetStatus("Starting run");
        if (verbose)
        {
            sink.AddVerboseDetail($"Starting run. bundle={bundlePath} task={taskPath} out={resolvedOutputPath}");
        }
        var workingDirectory = _fileSystem.Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _fileSystem.Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(workingDirectory);
        if (verbose)
        {
            sink.AddVerboseDetail($"Working directory: {workingDirectory}");
        }

        try
        {
            var commitPath = _fileSystem.Path.Combine(taskPath, "base_commit.txt");
            if (!_fileSystem.File.Exists(commitPath))
            {
                throw new FileNotFoundException("base_commit.txt not found", commitPath);
            }

            var commitHash = await _fileSystem.File.ReadAllTextAsync(commitPath);
            commitHash = commitHash.Trim();
            if (verbose)
            {
                sink.AddVerboseDetail($"Using base commit: {commitHash}");
            }

            sink.SetStatus("Cloning repository");
            await _gitRunner.RunAsync($"clone . \"{workingDirectory}\"");
            sink.SetStatus("Checking out base commit");
            await _gitRunner.RunAsync($"checkout {commitHash}", workingDirectory);

            var bundleId = _fileSystem.Path.GetFileName(bundlePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var sourcePracticesPath = _bundleResolver.Resolve(bundlePath);
            sink.SetStatus("Copying bundle practices");
            if (verbose)
            {
                sink.AddVerboseDetail($"Resolved source practices path: {sourcePracticesPath}");
            }
            var sourceBundleRoot = _fileSystem.Path.GetDirectoryName(sourcePracticesPath)
                ?? throw new InvalidOperationException($"Could not resolve bundle root from practices path: {sourcePracticesPath}");
            var sourceAgentsPath = _fileSystem.Path.Combine(sourceBundleRoot, "agents");

            var bundleRootRelativePath = _fileSystem.Path.Combine(".mystro", "promptopt", "bundles", bundleId);
            var bundlePracticesRootRelativePath = _fileSystem.Path.Combine(bundleRootRelativePath, "practices");
            var bundleAgentsRootRelativePath = _fileSystem.Path.Combine(bundleRootRelativePath, "agents");
            var workspacePracticesPath = _fileSystem.Path.Combine(workingDirectory, bundlePracticesRootRelativePath);
            CopyDirectory(sourcePracticesPath, workspacePracticesPath);
            if (verbose)
            {
                sink.AddVerboseDetail($"Copied practices into workspace: {workspacePracticesPath}");
            }

            if (!_fileSystem.Directory.Exists(sourceAgentsPath))
            {
                throw new DirectoryNotFoundException(
                    $"Agents directory is required for prompt optimization bundles but was not found: {sourceAgentsPath}");
            }

            sink.SetStatus("Copying bundle agents");
            var workspaceAgentsPath = _fileSystem.Path.Combine(workingDirectory, bundleAgentsRootRelativePath);
            CopyDirectory(sourceAgentsPath, workspaceAgentsPath);
            if (verbose)
            {
                sink.AddVerboseDetail($"Copied agents into workspace: {workspaceAgentsPath}");
            }
            ValidateRequiredAgentFiles(workspaceAgentsPath);
            if (verbose)
            {
                sink.AddVerboseDetail($"Validated required agent prompt files in: {workspaceAgentsPath}");
            }

            var practiceService = new PracticeService(_fileService, workspacePracticesPath);
            var practices = (await practiceService.GetPracticesAsync()).ToList();
            if (verbose)
            {
                sink.AddVerboseDetail($"Loaded {practices.Count} practices.");
            }

            var goalFilePath = _fileSystem.Path.Combine(taskPath, "goal.txt");
            var taskText = await _fileSystem.File.ReadAllTextAsync(goalFilePath);
            if (string.IsNullOrWhiteSpace(taskText))
            {
                throw new InvalidOperationException($"goal.txt is empty: {goalFilePath}");
            }
            sink.SetStatus("Loading goal");
            if (verbose)
            {
                sink.AddVerboseDetail($"Loaded goal file: {goalFilePath}");
            }

            if (!_fileSystem.Directory.Exists(resolvedOutputPath))
            {
                _fileSystem.Directory.CreateDirectory(resolvedOutputPath);
                if (verbose)
                {
                    sink.AddVerboseDetail($"Created output directory: {resolvedOutputPath}");
                }
            }
            else
            {
                if (verbose)
                {
                    sink.AddVerboseDetail($"Using existing output directory: {resolvedOutputPath}");
                }
            }

            CopyOptionalTaskArtifact(
                taskPath: taskPath,
                outDir: resolvedOutputPath,
                artifactName: "previous_run_results.json",
                verbose: verbose,
                statusSink: sink);

            _runContextAccessor.Current = new PromptOptRunContext(
                resolvedOutputPath,
                Capture: true,
                BundleId: bundleId,
                ApplySandboxPatchToSource: false
            );
            if (verbose)
            {
                sink.AddVerboseDetail($"Set run context. bundleId={bundleId}");
            }

            var currentDir = Directory.GetCurrentDirectory();
            try
            {
                if (verbose)
                {
                    sink.AddVerboseDetail($"Switching current directory: {currentDir} -> {workingDirectory}");
                }
                Directory.SetCurrentDirectory(workingDirectory);
                sink.SetStatus("Running agent orchestrator");
                await _agentOrchestrator.RunAsync(taskText, practices, bundleAgentsRootRelativePath);
                if (verbose)
                {
                    sink.AddVerboseDetail("Agent orchestrator completed.");
                }
            }
            finally
            {
                Directory.SetCurrentDirectory(currentDir);
                if (verbose)
                {
                    sink.AddVerboseDetail($"Restored current directory: {currentDir}");
                }
            }
        }
        finally
        {
            try
            {
                if (_fileSystem.Directory.Exists(workingDirectory))
                {
                    _fileSystem.Directory.Delete(workingDirectory, recursive: true);
                    if (verbose)
                    {
                        sink.AddVerboseDetail($"Cleaned up working directory: {workingDirectory}");
                    }
                }
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    sink.AddVerboseDetail($"Failed to clean working directory '{workingDirectory}': {ex.Message}");
                }
            }
        }

        sink.SetStatus("Running evaluation");
        sink.SetEvaluationState(Mystro.Presentation.Console.EvaluationPanelState.Running);
        await _runEvaluator.EvaluateAsync(resolvedOutputPath, bundlePath);
        sink.SetStatus("Evaluation completed");
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

    private void CopyOptionalTaskArtifact(
        string taskPath,
        string outDir,
        string artifactName,
        bool verbose,
        IPromptOptCliStatusSink statusSink)
    {
        var sourcePath = _fileSystem.Path.Combine(taskPath, artifactName);
        if (!_fileSystem.File.Exists(sourcePath))
        {
            if (verbose)
            {
                statusSink.AddVerboseDetail($"Optional task artifact not found: {sourcePath}");
            }
            return;
        }

        var destinationPath = _fileSystem.Path.Combine(outDir, artifactName);
        try
        {
            _fileSystem.File.Copy(sourcePath, destinationPath, overwrite: true);
            if (verbose)
            {
                statusSink.AddVerboseDetail($"Copied optional task artifact: {sourcePath} -> {destinationPath}");
            }
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                statusSink.AddVerboseDetail($"Failed to copy optional task artifact '{sourcePath}' to '{destinationPath}': {ex.Message}");
            }
        }
    }

    private void ValidateRequiredAgentFiles(string agentsDirectory)
    {
        foreach (var fileName in RequiredAgentPromptFiles)
        {
            var filePath = _fileSystem.Path.Combine(agentsDirectory, fileName);
            if (!_fileSystem.File.Exists(filePath))
            {
                throw new FileNotFoundException(
                    $"Prompt optimization bundles must include required agent file '{fileName}'.",
                    filePath);
            }
        }
    }
}
