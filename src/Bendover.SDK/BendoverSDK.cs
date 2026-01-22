using Bendover.Domain.Interfaces;
using System.Diagnostics;

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
        System.IO.File.WriteAllText(path, content);
    }

    public string Read(string path)
    {
        return System.IO.File.ReadAllText(path);
    }

    public bool Exists(string path)
    {
        return System.IO.File.Exists(path);
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
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string result = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return result;
    }
}
