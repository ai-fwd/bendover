using System.Diagnostics;
using System.Threading.Tasks;
using Bendover.Application.Interfaces;

namespace Bendover.Infrastructure.Services;

public class GitRunner : IGitRunner
{
    public async Task<string> RunAsync(string arguments, string? workingDirectory = null, string? standardInput = null)
    {
        return await ProcessRunner.RunAsync("git", arguments, workingDirectory, standardInput);
    }
}

public class DotNetRunner : IDotNetRunner
{
    public async Task<string> RunAsync(string arguments, string? workingDirectory = null)
    {
        return await ProcessRunner.RunAsync("dotnet", arguments, workingDirectory);
    }
}

public static class ProcessRunner
{
    public static async Task<string> RunAsync(string fileName, string arguments, string? workingDirectory = null, string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? ""
        };

        using var process = Process.Start(startInfo);
        if (process == null) return string.Empty;

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new System.Exception($"Command '{fileName} {arguments}' failed (Exit Code {process.ExitCode}):\n{error}");
        }

        return output;
    }
}
