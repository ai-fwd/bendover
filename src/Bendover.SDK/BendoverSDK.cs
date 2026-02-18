using Bendover.Domain.Interfaces;
using Bendover.Domain.Agentic;
using System.Diagnostics;

namespace Bendover.SDK;

public class BendoverSDK : IBendoverSDK
{
    public IFileSystem File { get; } = new FileSystem();
    public IGit Git { get; } = new GitSystem();
    public IShell Shell { get; } = new ShellSystem();
    public ISignal Signal { get; } = new SignalSystem();
}

public class FileSystem : IFileSystem
{
    public void Write(string path, string content)
    {
        var fullPath = ResolveWorkspacePath(path);
        System.IO.File.WriteAllText(fullPath, content);
    }

    public string Read(string path)
    {
        var fullPath = ResolveWorkspacePath(path);
        return System.IO.File.ReadAllText(fullPath);
    }

    public bool Exists(string path)
    {
        var fullPath = ResolveWorkspacePath(path);
        return System.IO.File.Exists(fullPath);
    }

    public void Delete(string path)
    {
        var fullPath = ResolveWorkspacePath(path);
        System.IO.File.Delete(fullPath);
    }

    private static string ResolveWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Path cannot be empty.");
        }

        var workspaceRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        var candidate = Path.IsPathRooted(path)
            ? path
            : Path.Combine(workspaceRoot, path);
        var fullPath = Path.GetFullPath(candidate);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var workspacePrefix = EnsureTrailingSeparator(workspaceRoot);

        if (!fullPath.StartsWith(workspacePrefix, comparison)
            && !string.Equals(fullPath, workspaceRoot, comparison))
        {
            throw new InvalidOperationException($"Path '{path}' resolves outside workspace root.");
        }

        return fullPath;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar)
            || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }
}

public class GitSystem : IGit
{
    public void Clone(string url)
    {
        Process.Start("git", $"clone {url}").WaitForExit();
    }

    public void Commit(string message)
    {
        Process.Start("git", $"commit -am \"{message}\"").WaitForExit();
    }

    public void Push()
    {
        Process.Start("git", "push").WaitForExit();
    }
}

public class ShellSystem : IShell
{
    public string Execute(string command)
    {
        if (!ShellCommandPolicy.TryValidateAllowedForEngineer(command, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Shell command failed (exit code {process.ExitCode}): {command}\n{stderr}\n{stdout}");
        }

        var output = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout}{stderr}";

        // Auto-surface read-only discovery output without requiring Console.WriteLine wrappers.
        if (!string.IsNullOrEmpty(output)
            && ShellCommandPolicy.IsReadOnlyCommand(command))
        {
            Console.Out.Write(output);
        }

        return output;
    }
}

public class SignalSystem : ISignal
{
    public void Done()
    {
        // Marker-only API used by ScriptRunner validation/metadata classification.
    }
}
