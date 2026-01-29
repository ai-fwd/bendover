using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Bendover.PromptOpt.CLI;

public class DotNetRunner : IDotNetRunner
{
    public async Task<string> RunTestsAsync(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "test",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start dotnet test.");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return $"Standard Output:{output}\nStandard Error:{error}";
    }
}
