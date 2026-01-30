using System.Diagnostics;
using System.Threading.Tasks;
using Bendover.Application.Interfaces;

namespace Bendover.Infrastructure.Services;

public class GitRunner : IGitRunner
{
    public async Task<string> RunAsync(string arguments)
    {
        return await ProcessRunner.RunAsync("git", arguments);
    }
}

public class DotNetRunner : IDotNetRunner
{
    public async Task<string> RunAsync(string arguments)
    {
        return await ProcessRunner.RunAsync("dotnet", arguments);
    }
}

public static class ProcessRunner
{
    public static async Task<string> RunAsync(string fileName, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) return string.Empty;

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
