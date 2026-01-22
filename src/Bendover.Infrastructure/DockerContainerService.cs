using Bendover.Domain.Interfaces;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Bendover.Infrastructure;

public class DockerContainerService : IContainerService
{
    private readonly DockerClient _client;
    private string? _containerId;
    private const string ImageName = "mcr.microsoft.com/dotnet/sdk:10.0"; // Base image, usually need custom one or install tool dynamically

    public DockerContainerService()
    {
        var dockerUri = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    public async Task StartContainerAsync()
    {
        // 1. Ensure Image Exists (simplified)
        // await _client.Images.CreateImageAsync(new ImagesCreateParameters { FromImage = ImageName, Tag = "latest" }, null, new Progress<JSONMessage>());

        // 2. Create Container
        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = ImageName,
            Cmd = new[] { "sleep", "infinity" }, // Keep alive
            HostConfig = new HostConfig
            {
                // Mounts would go here
            }
        });
        _containerId = response.ID;

        // 3. Start
        await _client.Containers.StartContainerAsync(_containerId, new ContainerStartParameters());

        // 4. Verify dotnet-script
        var check = await ExecuteCommandAsync("dotnet tool list -g");
        if (!check.Contains("dotnet-script"))
        {
            // Attempt to install it if missing (or throw error if strictly enforced)
            await ExecuteCommandAsync("dotnet tool install -g dotnet-script");
        }
    }

    public async Task<string> ExecuteScriptAsync(string scriptContent)
    {
        if (_containerId == null) throw new InvalidOperationException("Container not started");

        // 1. Write script to file inside container (simulated via echo or mount)
        // Ideally we mount the script, but for now we'll write it via shell for simplicity
        // Limitation: large scripts might fail via echo
        var escapedScript = scriptContent.Replace("\"", "\\\""); // Simple escaping
        await ExecuteCommandAsync($"echo \"{escapedScript}\" > /tmp/script.csx");

        // 2. Run dotnet script
        return await ExecuteCommandAsync("dotnet script /tmp/script.csx");
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

    private async Task<string> ExecuteCommandAsync(string command)
    {
        var execCreate = await _client.Exec.CreateContainerExecAsync(_containerId, new ContainerExecCreateParameters
        {
            Cmd = new[] { "/bin/bash", "-c", command },
            AttachStdout = true,
            AttachStderr = true
        });

        var stream = await _client.Exec.StartContainerExecAsync(execCreate.ID, new ContainerExecStartParameters() { Detach = false }, CancellationToken.None);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);
        return stdout + stderr;
    }
}
