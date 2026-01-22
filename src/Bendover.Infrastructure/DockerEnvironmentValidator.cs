using Bendover.Domain.Exceptions;
using Bendover.Domain.Interfaces;
using Docker.DotNet;
using System.Net.Sockets;

namespace Bendover.Infrastructure;

public class DockerEnvironmentValidator : IEnvironmentValidator
{
    private readonly DockerClient _client;

    public DockerEnvironmentValidator()
    {
        var dockerUri = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows)
            ? new Uri("npipe://./pipe/docker_engine")
            : new Uri("unix:///var/run/docker.sock");

        _client = new DockerClientConfiguration(dockerUri).CreateClient();
    }

    // Constructor for DI if needed later or for testing
    public DockerEnvironmentValidator(DockerClient client)
    {
        _client = client;
    }

    public async Task ValidateAsync()
    {
        try
        {
            await _client.System.PingAsync();
        }
        catch (Exception ex)
        {
            var baseEx = ex is AggregateException ae ? ae.InnerException ?? ex : ex;
            
            if (IsDockerConnectionFailure(baseEx))
            {
                var isLinux = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux);
                var isWsl = isLinux && File.Exists("/proc/version") && File.ReadAllText("/proc/version").Contains("WSL");
                
                string failureReason = $"Docker Daemon unreachable ({baseEx.GetType().Name}: {baseEx.Message}).";
                string remediation = "";

                if (isWsl)
                {
                    remediation = "It appears you are running in WSL. If using Docker Desktop on Windows, please enable 'WSL Integration' in Settings > Resources > WSL Integration for this distro.";
                }
                else if (isLinux) 
                {
                    if (baseEx.Message.Contains("Permission denied") || baseEx.Message.Contains("EACCES"))
                    {
                        remediation = "Permission denied accessing /var/run/docker.sock. Try running: 'sudo chmod 666 /var/run/docker.sock'.";
                    }
                    else
                    {
                         remediation = "Ensure Docker is running (sudo service docker start) or check DOCKER_HOST.";
                    }
                }
                else
                {
                    remediation = "Ensure Docker Desktop is running.";
                }

                throw new DockerUnavailableException($"{failureReason} {remediation}", baseEx);
            }

            // If it's some other non-connection error (e.g. 500 from daemon), we might still want to bubble up, 
            // but for now wrapping in DockerUnavailableException is safest for the consumer.
            throw new DockerUnavailableException($"Docker check failed unexpectedly: {baseEx.Message}", baseEx);
        }
    }

    private bool IsDockerConnectionFailure(Exception ex)
    {
        // Common connection failures
        return ex is HttpRequestException || 
               ex is SocketException || 
               ex is DockerApiException || 
               ex is IOException || 
               (ex.InnerException != null && IsDockerConnectionFailure(ex.InnerException)) ||
               ex.Message.Contains("Connection refused") ||
               ex.Message.Contains("Permission denied");
    }
}
