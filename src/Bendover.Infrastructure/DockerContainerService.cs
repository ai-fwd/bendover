using System.Text;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Bendover.Infrastructure;

public class DockerContainerService : IContainerService
{
    private readonly DockerClient _client;
    private string? _containerId;
    private const string ImageName = "mcr.microsoft.com/dotnet/sdk:10.0";
    private const string InputRepoPath = "/input/repo";
    private const string WorkspacePath = "/workspace";
    private const string ScriptBodyPath = "/workspace/script_body.csx";

    public DockerContainerService()
    {
        var dockerUri = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    public async Task StartContainerAsync(SandboxExecutionSettings settings)
    {
        if (_containerId is not null)
        {
            throw new InvalidOperationException("Container is already started.");
        }

        var sourceRepositoryPath = Path.GetFullPath(settings.SourceRepositoryPath);
        if (!Directory.Exists(sourceRepositoryPath))
        {
            throw new DirectoryNotFoundException($"Source repository path not found: {sourceRepositoryPath}");
        }

        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = ImageName,
            Cmd = new[] { "sleep", "infinity" },
            HostConfig = new HostConfig
            {
                Mounts = new List<Mount>
                {
                    new()
                    {
                        Type = "bind",
                        Source = sourceRepositoryPath,
                        Target = InputRepoPath,
                        ReadOnly = true
                    }
                }
            }
        });

        _containerId = response.ID;
        await _client.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());

        var copyResult = await ExecuteCommandAsync($"rm -rf {WorkspacePath} && mkdir -p {WorkspacePath} && cp -a {InputRepoPath}/. {WorkspacePath}");
        if (copyResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to initialize workspace.\n{copyResult.CombinedOutput}");
        }

        var safeDirectoryResult = await ExecuteCommandAsync($"git config --global --add safe.directory {WorkspacePath}");
        if (safeDirectoryResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to configure git safe.directory for workspace.\n{safeDirectoryResult.CombinedOutput}");
        }

        if (!string.IsNullOrWhiteSpace(settings.BaseCommit))
        {
            // Restore tracked files to the run's base commit before executing any turns.
            var resetResult = await ExecuteCommandAsync($"cd {WorkspacePath} && git reset --hard {settings.BaseCommit}");
            if (resetResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to reset workspace to base commit.\n{resetResult.CombinedOutput}");
            }
        }

        if (settings.CleanWorkspace || !string.IsNullOrWhiteSpace(settings.BaseCommit))
        {
            // Remove untracked files while preserving ignored local config (for example `.env`).
            var cleanResult = await ExecuteCommandAsync($"cd {WorkspacePath} && git clean -fd");
            if (cleanResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to clean workspace.\n{cleanResult.CombinedOutput}");
            }
        }

        var buildRunnerResult = await ExecuteCommandAsync($"cd {WorkspacePath} && dotnet build src/Bendover.ScriptRunner/Bendover.ScriptRunner.csproj -c Debug -v minimal");
        if (buildRunnerResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to build ScriptRunner inside sandbox.\n{buildRunnerResult.CombinedOutput}");
        }
    }

    public async Task<SandboxExecutionResult> ExecuteScriptBodyAsync(string bodyContent)
    {
        EnsureContainerStarted();

        var encodedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(bodyContent));
        var writeBodyResult = await ExecuteCommandAsync($"printf '%s' '{encodedBody}' | base64 -d > {ScriptBodyPath}");
        if (writeBodyResult.ExitCode != 0)
        {
            return writeBodyResult;
        }

        return await ExecuteCommandAsync($"cd {WorkspacePath} && dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file {ScriptBodyPath}");
    }

    public async Task StopContainerAsync()
    {
        if (_containerId != null)
        {
            await _client.Containers.StopContainerAsync(_containerId, new ContainerStopParameters());
            await _client.Containers.RemoveContainerAsync(_containerId, new ContainerRemoveParameters { Force = true });
            _containerId = null;
        }
    }

    public async Task<SandboxExecutionResult> ExecuteCommandAsync(string command)
    {
        EnsureContainerStarted();

        var execCreate = await _client.Exec.CreateContainerExecAsync(_containerId, new ContainerExecCreateParameters
        {
            Cmd = new[] { "/bin/bash", "-c", command },
            AttachStdout = true,
            AttachStderr = true
        });

        var stream = await _client.Exec.StartContainerExecAsync(execCreate.ID, new ContainerExecStartParameters() { Detach = false }, CancellationToken.None);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);
        var inspect = await _client.Exec.InspectContainerExecAsync(execCreate.ID);
        var exitCode = inspect.ExitCode.HasValue ? (int)inspect.ExitCode.Value : -1;
        var combinedOutput = $"{stdout}{stderr}";
        return new SandboxExecutionResult(exitCode, stdout, stderr, combinedOutput);
    }

    private void EnsureContainerStarted()
    {
        if (_containerId is null)
        {
            throw new InvalidOperationException("Container not started");
        }
    }
}
