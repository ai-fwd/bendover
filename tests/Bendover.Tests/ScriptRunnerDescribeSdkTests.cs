using System.Diagnostics;

namespace Bendover.Tests;

public class ScriptRunnerDescribeSdkTests
{
    private const string ContractHeading = "# SDK Tool Usage Contract (Auto-generated)";
    private const string ToolsMarkdownRelativePath = ".bendover/agents/tools.md";

    [Fact]
    public async Task DescribeSdk_WithOut_WritesMarkdownFile()
    {
        var repoRoot = FindRepoRoot();
        var runnerDll = await EnsureScriptRunnerOutputAsync(repoRoot);
        var outputPath = Path.Combine(Path.GetTempPath(), $"sdk-surface-{Guid.NewGuid():N}.md");

        try
        {
            var result = await RunProcessAsync(
                "dotnet",
                new[]
                {
                    runnerDll,
                    "--describe-sdk",
                    "--out",
                    outputPath
                },
                repoRoot);

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(outputPath), $"Expected output file at {outputPath}");
            var markdown = File.ReadAllText(outputPath);
            Assert.Contains(ContractHeading, markdown, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }

    [Fact]
    public async Task DescribeSdk_WithoutOut_WritesToStdout()
    {
        var repoRoot = FindRepoRoot();
        var runnerDll = await EnsureScriptRunnerOutputAsync(repoRoot);
        var result = await RunProcessAsync(
            "dotnet",
            new[]
            {
                runnerDll,
                "--describe-sdk"
            },
            repoRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(ContractHeading, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DescribeSdk_RejectsLegacyDescribeSdkOutputFlag()
    {
        var repoRoot = FindRepoRoot();
        var runnerDll = await EnsureScriptRunnerOutputAsync(repoRoot);
        var result = await RunProcessAsync(
            "dotnet",
            new[]
            {
                runnerDll,
                "--describe-sdk",
                "--describe-sdk-output",
                "ignored.md"
            },
            repoRoot);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Unknown argument '--describe-sdk-output'", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Build_GeneratesToolsMarkdownUnderBendoverAgents()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "Bendover.ScriptRunner", "Bendover.ScriptRunner.csproj");
        var outputPath = Path.Combine(repoRoot, ToolsMarkdownRelativePath);

        var exitCode = await RunProcessWithoutRedirectAsync(
            "dotnet",
            new[] { "build", projectPath, "-c", "Debug", "-v", "minimal" },
            repoRoot,
            timeout: TimeSpan.FromMinutes(3));

        Assert.True(exitCode == 0, "ScriptRunner build failed.");
        Assert.True(File.Exists(outputPath), $"Expected tools markdown at {outputPath}");
        var markdown = File.ReadAllText(outputPath);
        Assert.Contains(ContractHeading, markdown, StringComparison.Ordinal);
    }

    private static async Task<string> EnsureScriptRunnerOutputAsync(string repoRoot)
    {
        var projectPath = Path.Combine(repoRoot, "src", "Bendover.ScriptRunner", "Bendover.ScriptRunner.csproj");
        var outputDllPath = Path.Combine(repoRoot, "src", "Bendover.ScriptRunner", "bin", "Debug", "net10.0", "Bendover.ScriptRunner.dll");

        if (!File.Exists(outputDllPath))
        {
            var buildResult = await RunProcessAsync(
                "dotnet",
                new[] { "build", projectPath, "-c", "Debug", "-v", "minimal" },
                repoRoot,
                timeout: TimeSpan.FromMinutes(3));
            Assert.True(buildResult.ExitCode == 0, $"ScriptRunner build failed.\nstdout:\n{buildResult.Stdout}\nstderr:\n{buildResult.Stderr}");
        }

        Assert.True(File.Exists(outputDllPath), $"ScriptRunner output missing at {outputDllPath}");
        return outputDllPath;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bendover.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Bendover.sln from test base directory.");
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process {fileName}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
        var waitTask = process.WaitForExitAsync();
        var completedTask = await Task.WhenAny(waitTask, Task.Delay(effectiveTimeout));
        if (completedTask != waitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }

            throw new TimeoutException($"Command timed out: {fileName} {string.Join(' ', arguments)}");
        }

        await waitTask;
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<int> RunProcessWithoutRedirectAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process {fileName}.");
        }

        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(2);
        var waitTask = process.WaitForExitAsync();
        var completedTask = await Task.WhenAny(waitTask, Task.Delay(effectiveTimeout));
        if (completedTask != waitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }

            throw new TimeoutException($"Command timed out: {fileName} {string.Join(' ', arguments)}");
        }

        await waitTask;
        return process.ExitCode;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
