using Bendover.Domain.Interfaces;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Bendover.SDK;

public class BendoverSDK : IBendoverSDK
{
    public IFileSystem File { get; } = new FileSystem();
    public IGit Git { get; } = new GitSystem();
    public IShell Shell { get; } = new ShellSystem();
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
    private static readonly Regex SegmentSplitRegex = new(@"\|\||&&|\||;", RegexOptions.Compiled);
    private static readonly string[] ReadOnlyPrefixes =
    {
        "cat ",
        "sed ",
        "ls",
        "find ",
        "rg ",
        "grep ",
        "head ",
        "tail ",
        "wc ",
        "pwd",
        "git status",
        "git diff",
        "git log",
        "git show",
        "git rev-parse",
        "which ",
        "dotnet --info",
        "dotnet --list-sdks",
        "dotnet --list-runtimes"
    };

    private static readonly string[] MutatingFragments =
    {
        " rm ",
        " rm\t",
        "rm -",
        "mv ",
        "cp ",
        "mkdir ",
        "touch ",
        "truncate ",
        "chmod ",
        "chown ",
        "ln ",
        "git reset",
        "git clean",
        "git checkout",
        "git apply",
        "git am",
        "git commit",
        "git push",
        "git merge",
        "git rebase",
        "git tag"
    };

    public string Execute(string command)
    {
        if (!IsAllowedShellCommand(command, out var reason))
        {
            throw new InvalidOperationException($"Shell command rejected: {reason}");
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

        return string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : $"{stdout}{stderr}";
    }

    private static bool IsAllowedShellCommand(string command, out string reason)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            reason = "command is empty";
            return false;
        }

        var trimmed = command.Trim();
        var lower = $" {trimmed.ToLowerInvariant()} ";

        if (trimmed.Contains('>'))
        {
            reason = "redirect operators are not allowed";
            return false;
        }

        if (MutatingFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal)))
        {
            reason = "mutating shell commands are not allowed";
            return false;
        }

        var segments = SegmentSplitRegex
            .Split(trimmed)
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        if (segments.Length == 0)
        {
            reason = "command has no executable segments";
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsVerificationSegment(segment) || IsReadOnlySegment(segment))
            {
                continue;
            }

            reason = $"segment '{segment}' is not in the read/verification allowlist";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsVerificationSegment(string segment)
    {
        return Regex.IsMatch(
            segment,
            @"^dotnet\s+(build|test)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsReadOnlySegment(string segment)
    {
        return ReadOnlyPrefixes.Any(prefix => segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
