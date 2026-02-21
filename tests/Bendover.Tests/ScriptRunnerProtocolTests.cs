using System.Diagnostics;
using System.Text.Json;

namespace Bendover.Tests;

public class ScriptRunnerProtocolTests
{
    private static readonly SemaphoreSlim BuildSemaphore = new(1, 1);
    private static bool _scriptRunnerBuilt;

    [Fact]
    public async Task ScriptRunner_WritesMutationActionMetadata_ToResultFile()
    {
        var targetFile = $"tmp-script-runner-protocol-{Guid.NewGuid():N}.txt";
        var repoRoot = FindRepoRoot();
        var targetPath = Path.Combine(repoRoot, targetFile);
        var body = $"""
sdk.File.Write("{targetFile}", "x");
""";

        try
        {
            var (_, action) = await RunScriptWithResultAsync(body);

            Assert.Equal("mutation_write", action.kind);
            Assert.Equal("sdk.File.Write", action.command);
        }
        finally
        {
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
        }
    }

    [Fact]
    public async Task ScriptRunner_WritesVerificationBuildMetadata_ToResultFile()
    {
        var body = """
sdk.Run("dotnet build --help");
""";

        var (_, action) = await RunScriptWithResultAsync(body);

        Assert.Equal("verification_build", action.kind);
        Assert.Equal("dotnet build --help", action.command);
    }

    [Fact]
    public async Task ScriptRunner_WritesDiscoveryMetadata_ToResultFile()
    {
        var body = """
sdk.Shell.Execute("ls -la");
""";

        var (_, action) = await RunScriptWithResultAsync(body);

        Assert.Equal("discovery_shell", action.kind);
        Assert.Equal("ls -la", action.command);
    }

    [Fact]
    public async Task ScriptRunner_WritesStepPlanAndToolCallMetadata_ToResultFile()
    {
        var body = """
var __stepPlan = "Need to list files to find README.md";
sdk.Shell.Execute("ls -la");
""";

        var (_, action) = await RunScriptWithResultAsync(body);

        Assert.Equal("Need to list files to find README.md", action.step_plan);
        Assert.Equal("sdk.Shell.Execute(\"ls -la\")", action.tool_call);
    }

    [Fact]
    public async Task ScriptRunner_WritesCompleteMetadata_ToResultFile()
    {
        var body = """
sdk.Signal.Done();
""";

        var (_, action) = await RunScriptWithResultAsync(body);

        Assert.Equal("complete", action.kind);
        Assert.Equal("sdk.Signal.Done", action.command);
    }

    [Fact]
    public async Task ScriptRunner_WritesCompleteMetadata_ForDoneShortcut_ToResultFile()
    {
        var body = """
sdk.Done();
""";

        var (_, action) = await RunScriptWithResultAsync(body);

        Assert.Equal("complete", action.kind);
        Assert.Equal("sdk.Done", action.command);
    }

    [Fact]
    public async Task ScriptRunner_WritesBestEffortMetadata_WhenValidationFails()
    {
        var body = """
sdk.File.Write("a.txt", "x");
sdk.File.Write("b.txt", "y");
""";

        var (result, action) = await RunScriptWithResultAsync(body);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("mutation_write", action.kind);
        Assert.Equal("sdk.File.Write", action.command);
    }

    private static async Task<(ProcessResult Result, ScriptRunnerActionResult action)> RunScriptWithResultAsync(string body)
    {
        var repoRoot = FindRepoRoot();
        await EnsureScriptRunnerBuiltAsync(repoRoot);

        var bodyFile = Path.Combine(Path.GetTempPath(), $"script-runner-protocol-{Guid.NewGuid():N}.csx");
        var resultFile = Path.Combine(Path.GetTempPath(), $"script-runner-protocol-result-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(bodyFile, body);

        try
        {
            var runnerDll = Path.Combine(
                repoRoot,
                "src",
                "Bendover.ScriptRunner",
                "bin",
                "Debug",
                "net10.0",
                "Bendover.ScriptRunner.dll");

            var result = await RunProcessAsync(
                "dotnet",
                new[] { runnerDll, "--body-file", bodyFile, "--result-file", resultFile },
                repoRoot,
                timeout: TimeSpan.FromMinutes(2));

            Assert.True(File.Exists(resultFile), "Result file was not created.");
            var json = await File.ReadAllTextAsync(resultFile);
            var action = JsonSerializer.Deserialize<ScriptRunnerActionResult>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(action);

            return (result, action!);
        }
        finally
        {
            if (File.Exists(bodyFile))
            {
                File.Delete(bodyFile);
            }

            if (File.Exists(resultFile))
            {
                File.Delete(resultFile);
            }
        }
    }

    private static async Task EnsureScriptRunnerBuiltAsync(string repoRoot)
    {
        if (_scriptRunnerBuilt)
        {
            return;
        }

        await BuildSemaphore.WaitAsync();
        try
        {
            if (_scriptRunnerBuilt)
            {
                return;
            }

            var projectPath = Path.Combine(repoRoot, "src", "Bendover.ScriptRunner", "Bendover.ScriptRunner.csproj");
            var buildResult = await RunProcessAsync(
                "dotnet",
                new[] { "build", projectPath, "-c", "Debug", "-v", "normal", "-m:1", "/p:UseSharedCompilation=false" },
                repoRoot,
                timeout: TimeSpan.FromMinutes(3));

            if (buildResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"ScriptRunner build failed.\nSTDOUT:\n{buildResult.Stdout}\nSTDERR:\n{buildResult.Stderr}");
            }

            _scriptRunnerBuilt = true;
        }
        finally
        {
            BuildSemaphore.Release();
        }
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

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
    private sealed record ScriptRunnerActionResult(
        string kind,
        string? command,
        string? step_plan,
        string? tool_call);
}
