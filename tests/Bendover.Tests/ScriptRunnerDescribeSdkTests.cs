using System.Diagnostics;
using Bendover.SDK;

namespace Bendover.Tests;

public class ScriptRunnerDescribeSdkTests
{
    private const string ToolsMarkdownRelativePath = ".bendover/agents/tools.md";

    [Fact]
    public void SdkSurfaceDescriber_BuildMarkdown_ContainsContractHeading()
    {
        var markdown = SdkSurfaceDescriber.BuildMarkdown();
        Assert.Contains(SdkSurfaceDescriber.ContractHeading, markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkSurfaceDescriber_BuildMarkdown_ContainsDeleteSurface()
    {
        var markdown = SdkSurfaceDescriber.BuildMarkdown();
        Assert.Contains("Void Delete(String path)", markdown, StringComparison.Ordinal);
        Assert.Contains("Void DeleteFile(String path)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildSdk_GeneratesToolsMarkdownUnderBendoverAgents()
    {
        var repoRoot = FindRepoRoot();
        var projectPath = Path.Combine(repoRoot, "src", "Bendover.SDK", "Bendover.SDK.csproj");
        var outputPath = Path.Combine(repoRoot, ToolsMarkdownRelativePath);

        var exitCode = await RunProcessWithoutRedirectAsync(
            "dotnet",
            new[] { "build", projectPath, "-c", "Debug", "-v", "minimal" },
            repoRoot,
            timeout: TimeSpan.FromMinutes(3));

        Assert.True(exitCode == 0, "SDK build failed.");
        Assert.True(File.Exists(outputPath), $"Expected tools markdown at {outputPath}");
        var markdown = File.ReadAllText(outputPath);
        Assert.Contains(SdkSurfaceDescriber.ContractHeading, markdown, StringComparison.Ordinal);
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
}
