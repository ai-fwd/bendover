using System.Diagnostics;

namespace Bendover.Tests;

public class ScriptRunnerValidationTests
{
    private static readonly SemaphoreSlim BuildSemaphore = new(1, 1);
    private static bool _scriptRunnerBuilt;

    [Fact]
    public async Task ScriptBody_WithSingleWriteAndFileInspection_ShouldSucceed()
    {
        var repoRoot = FindRepoRoot();
        var targetFileName = $"tmp-script-runner-write-{Guid.NewGuid():N}.txt";
        var targetPath = Path.Combine(repoRoot, targetFileName);
        var body = $"""
var target = "{targetFileName}";
var exists = sdk.File.Exists(target);
sdk.File.Write(target, "ok");
""";

        try
        {
            var result = await RunScriptAsync(body);
            Assert.Equal(0, result.ExitCode);
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
    public async Task ScriptBody_WithSingleDeleteAndReadOnlySteps_ShouldSucceed()
    {
        var repoRoot = FindRepoRoot();
        var targetFileName = $"tmp-script-runner-delete-{Guid.NewGuid():N}.txt";
        var targetPath = Path.Combine(repoRoot, targetFileName);
        await File.WriteAllTextAsync(targetPath, "to delete");

        var body = $"""
var target = "{targetFileName}";
var exists = sdk.File.Exists(target);
var content = exists ? sdk.File.Read(target) : "";
sdk.File.Delete(target);
""";

        var result = await RunScriptAsync(body);
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(targetPath));
    }

    [Fact]
    public async Task ScriptBody_WithTypeDeclaration_ShouldBeRejected()
    {
        var body = """
public class BadType
{
}
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(
            "Engineer body rejected: contains namespace/type/member declarations",
            result.CombinedOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptBody_WithTwoWrites_ShouldBeRejected()
    {
        var body = """
sdk.File.Write("a.txt", "a");
sdk.File.Write("b.txt", "b");
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple mutation actions", result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptBody_WithWriteAndDelete_ShouldBeRejected()
    {
        var body = """
sdk.File.Write("a.txt", "a");
sdk.File.Delete("a.txt");
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple mutation actions", result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptBody_WithMutatingShellCommand_ShouldBeRejected()
    {
        var body = """
sdk.Shell.Execute("rm -f a.txt");
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("mutating and not allowed", result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptBody_WithVerificationOnlyShellStep_ShouldSucceed()
    {
        var body = """
sdk.Run("dotnet build --help");
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ScriptBody_WithDiscoveryOnlyShellStep_ShouldSucceed()
    {
        var body = """
sdk.Shell.Execute("ls -la");
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ScriptBody_WithDoneSignalStep_ShouldSucceed()
    {
        var body = """
sdk.Signal.Done();
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ScriptBody_WithDoneShortcutStep_ShouldSucceed()
    {
        var body = """
sdk.Done();
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task ScriptBody_WithDiscoveryAndDone_ShouldBeRejected()
    {
        var body = """
sdk.Shell.Execute("ls -la");
sdk.Signal.Done();
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple actionable steps", result.CombinedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScriptBody_WithWriteAndDone_ShouldBeRejected()
    {
        var body = """
sdk.File.Write("a.txt", "a");
sdk.Signal.Done();
""";

        var result = await RunScriptAsync(body);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("multiple actionable steps", result.CombinedOutput, StringComparison.Ordinal);
    }

    private static async Task<ProcessResult> RunScriptAsync(string body)
    {
        var repoRoot = FindRepoRoot();
        await EnsureScriptRunnerBuiltAsync(repoRoot);

        var bodyFile = Path.Combine(Path.GetTempPath(), $"script-runner-validation-{Guid.NewGuid():N}.csx");
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

            return await RunProcessAsync(
                "dotnet",
                new[] { runnerDll, "--body-file", bodyFile },
                repoRoot,
                timeout: TimeSpan.FromMinutes(2));
        }
        finally
        {
            if (File.Exists(bodyFile))
            {
                File.Delete(bodyFile);
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

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
    {
        public string CombinedOutput => $"{Stdout}{Stderr}";
    }
}
