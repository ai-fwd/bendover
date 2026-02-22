using System.Diagnostics;
using System.Text.Json;

namespace Bendover.Tests;

public class ScriptRunnerProtocolTests
{
    private static readonly SemaphoreSlim BuildSemaphore = new(1, 1);
    private static bool _scriptRunnerBuilt;

    [Fact]
    public async Task ScriptRunner_WritesWriteFileMetadata_ToResultFile()
    {
        var targetFile = $"tmp-script-runner-protocol-{Guid.NewGuid():N}.txt";
        var repoRoot = FindRepoRoot();
        var targetPath = Path.Combine(repoRoot, targetFile);
        var body = $"""
sdk.WriteFile("{targetFile}", "x");
""";

        try
        {
            var (_, action) = await RunScriptWithResultAsync(body);

            Assert.Equal("write_file", action.action_name);
            Assert.False(action.is_done);
            Assert.Equal("sdk.WriteFile", action.command);
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
    public async Task ScriptRunner_WritesLocateFileMetadata_WithStepPlanAndToolCall()
    {
        var body = """
var __stepPlan = "Locate orchestrator file";
sdk.LocateFile("AgentOrchestrator.cs");
""";

        var (_, action) = await RunScriptWithResultAsync(body);

        Assert.Equal("locate_file", action.action_name);
        Assert.False(action.is_done);
        Assert.Equal("sdk.LocateFile", action.command);
        Assert.Equal("Locate orchestrator file", action.step_plan);
        Assert.Equal("sdk.LocateFile(\"AgentOrchestrator.cs\")", action.tool_call);
    }

    [Fact]
    public async Task ScriptRunner_WritesDoneMetadata_ToResultFile()
    {
        var body = """
sdk.Done();
""";

        var (_, action) = await RunScriptWithResultAsync(body);

        Assert.Equal("done", action.action_name);
        Assert.True(action.is_done);
        Assert.Equal("sdk.Done", action.command);
    }

    [Fact]
    public async Task ScriptRunner_WritesUnknownMetadata_WhenValidationFails()
    {
        var body = """
sdk.Shell.Execute("ls -la");
""";

        var (result, action) = await RunScriptWithResultAsync(body);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("unknown", action.action_name);
        Assert.False(action.is_done);
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
        string action_name,
        bool is_done,
        string? command,
        string? step_plan,
        string? tool_call);
}
