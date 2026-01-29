using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Bendover.PromptOpt.CLI;

public class GitRunner : IGitRunner
{
    public async Task CheckoutAsync(string commitHash, string workingDirectory)
    {
        // Clone current repo to workingDirectory
        // Assumes CLI is run from repo root or a place where "." is the repo.
        var cloneStartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"clone . \"{workingDirectory}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var cloneProcess = Process.Start(cloneStartInfo);
        if (cloneProcess == null) throw new InvalidOperationException("Failed to start git clone.");
        
        await cloneProcess.WaitForExitAsync();
        if (cloneProcess.ExitCode != 0)
        {
            var error = await cloneProcess.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"Git clone failed: {error}");
        }

        // Checkout commit
        var checkoutStartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = $"checkout {commitHash}",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var checkoutProcess = Process.Start(checkoutStartInfo);
        if (checkoutProcess == null) throw new InvalidOperationException("Failed to start git checkout.");

        await checkoutProcess.WaitForExitAsync();
        if (checkoutProcess.ExitCode != 0)
        {
             var error = await checkoutProcess.StandardError.ReadToEndAsync();
             throw new InvalidOperationException($"Git checkout failed: {error}");
        }
    }

    public async Task<string> GetDiffAsync(string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "diff",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process == null) throw new InvalidOperationException("Failed to start git diff.");

        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}
