using System.Text.Json;
using Bendover.Application.Interfaces;
using IOAbstractions = System.IO.Abstractions;

namespace Bendover.PromptOpt.CLI;

public class RunScoringOrchestrator
{
    private static readonly string[] CopiedRunArtifacts =
    {
        "goal.txt",
        "base_commit.txt",
        "bundle_id.txt",
        "run_meta.json",
        "prompts.json",
        "outputs.json",
        "git_diff.patch",
        "dotnet_build.txt",
        "dotnet_build_error.txt",
        "dotnet_test.txt",
        "dotnet_test_error.txt",
        "sdk_surface_context.md"
    };

    private readonly IOAbstractions.IFileSystem _fileSystem;
    private readonly IPromptOptRunEvaluator _runEvaluator;

    public RunScoringOrchestrator(
        IOAbstractions.IFileSystem fileSystem,
        IPromptOptRunEvaluator runEvaluator)
    {
        _fileSystem = fileSystem;
        _runEvaluator = runEvaluator;
    }

    public async Task<string> ScoreAsync(string runId, string? bundleOverridePath, string? outputPath, bool verbose = false)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new ArgumentException("runId is required.", nameof(runId));
        }

        var repoRoot = Directory.GetCurrentDirectory();
        var runsRoot = _fileSystem.Path.Combine(repoRoot, ".bendover", "promptopt", "runs");
        var runDirectory = _fileSystem.Path.Combine(runsRoot, runId.Trim());
        if (!_fileSystem.Directory.Exists(runDirectory))
        {
            throw new DirectoryNotFoundException($"Run directory not found for run id '{runId}': {runDirectory}");
        }

        var bundlePath = ResolveBundlePath(repoRoot, runDirectory, bundleOverridePath);
        ValidateBundlePath(bundlePath, runDirectory);

        var resolvedOutputPath = ResolveOutputPath(repoRoot, runDirectory, outputPath);
        PrepareOutputArtifacts(runDirectory, resolvedOutputPath);

        Log(verbose, $"Scoring existing run. run_id={runId} bundle={bundlePath} out={resolvedOutputPath}");
        await _runEvaluator.EvaluateAsync(resolvedOutputPath, bundlePath);
        Log(verbose, "Scoring completed.");

        return resolvedOutputPath;
    }

    private string ResolveBundlePath(string repoRoot, string runDirectory, string? bundleOverridePath)
    {
        if (!string.IsNullOrWhiteSpace(bundleOverridePath))
        {
            return ResolvePath(repoRoot, bundleOverridePath);
        }

        var bundleId = ReadBundleId(runDirectory);
        if (string.IsNullOrWhiteSpace(bundleId)
            || string.Equals(bundleId, "current", StringComparison.OrdinalIgnoreCase)
            || string.Equals(bundleId, "default", StringComparison.OrdinalIgnoreCase))
        {
            return _fileSystem.Path.Combine(repoRoot, ".bendover");
        }

        return _fileSystem.Path.Combine(repoRoot, ".bendover", "promptopt", "bundles", bundleId);
    }

    private string ResolveOutputPath(string repoRoot, string runDirectory, string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return runDirectory;
        }

        return ResolvePath(repoRoot, outputPath);
    }

    private static string ResolvePath(string repoRoot, string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(repoRoot, path));
    }

    private string? ReadBundleId(string runDirectory)
    {
        var bundleIdPath = _fileSystem.Path.Combine(runDirectory, "bundle_id.txt");
        if (_fileSystem.File.Exists(bundleIdPath))
        {
            var bundleId = _fileSystem.File.ReadAllText(bundleIdPath).Trim();
            if (!string.IsNullOrWhiteSpace(bundleId))
            {
                return bundleId;
            }
        }

        var metaPath = _fileSystem.Path.Combine(runDirectory, "run_meta.json");
        if (!_fileSystem.File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(_fileSystem.File.ReadAllText(metaPath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("bundle_id", out var bundleSnake)
                && bundleSnake.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(bundleSnake.GetString()))
            {
                return bundleSnake.GetString()!.Trim();
            }

            if (doc.RootElement.TryGetProperty("bundleId", out var bundleCamel)
                && bundleCamel.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(bundleCamel.GetString()))
            {
                return bundleCamel.GetString()!.Trim();
            }
        }
        catch
        {
            // Ignore invalid run_meta.json and treat as "current".
        }

        return null;
    }

    private void ValidateBundlePath(string bundlePath, string runDirectory)
    {
        if (!_fileSystem.Directory.Exists(bundlePath))
        {
            throw new DirectoryNotFoundException(
                $"Resolved bundle path does not exist: {bundlePath}. Run directory: {runDirectory}");
        }

        var practicesPath = _fileSystem.Path.Combine(bundlePath, "practices");
        if (!_fileSystem.Directory.Exists(practicesPath))
        {
            throw new DirectoryNotFoundException(
                $"Resolved bundle path is missing practices directory: {practicesPath}");
        }
    }

    private void PrepareOutputArtifacts(string runDirectory, string outputDirectory)
    {
        if (PathsEqual(runDirectory, outputDirectory))
        {
            return;
        }

        _fileSystem.Directory.CreateDirectory(outputDirectory);

        foreach (var artifact in CopiedRunArtifacts)
        {
            var source = _fileSystem.Path.Combine(runDirectory, artifact);
            if (!_fileSystem.File.Exists(source))
            {
                continue;
            }

            var destination = _fileSystem.Path.Combine(outputDirectory, artifact);
            _fileSystem.File.Copy(source, destination, overwrite: true);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var normalizedLeft = Path.GetFullPath(left)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRight = Path.GetFullPath(right)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
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
