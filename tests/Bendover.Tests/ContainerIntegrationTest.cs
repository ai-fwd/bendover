using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using System.Net.Sockets;
using Xunit;

namespace Bendover.Tests;

public class ContainerIntegrationTest : IAsyncLifetime
{
    private IContainer? _container;
    private const string SdkPath = "/app/sdk";

    public async Task InitializeAsync()
    {
        // 0. Verify Environment
        var validator = new Bendover.Infrastructure.DockerEnvironmentValidator();
        try
        {
            await validator.ValidateAsync();
        }
        catch (Exception ex)
        {
             // Skip test if environment is not ready (VS/CI behavior) or fail with clear message
             // In XUnit, usually we throw standard exceptions.
             throw new InvalidOperationException($"Integration Test Environment Error: {ex.Message}", ex);
        }

        // Must point to the actual built SDK DLL location. 
        // For this test to pass, the SDK project must be built first.
        var localSdkPath = Path.GetFullPath("../../../src/Bendover.SDK/bin/Debug/net10.0");
        
        try 
        {
            // Suppress obsolescence warning for now as API exploration is limited without docs
            #pragma warning disable CS0618 
            _container = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/dotnet/sdk:10.0")
                .WithCommand("sleep", "infinity")
                .WithBindMount(localSdkPath, SdkPath)
                .Build();
            #pragma warning restore CS0618

            await _container.StartAsync();
            
            // Install dotnet-script if not present (simplified for test efficiency, usually baked in image)
            await _container.ExecAsync(new[] { "dotnet", "tool", "install", "-g", "dotnet-script" });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to initialize Docker container. Error: {ex.GetType().Name} - {ex.Message}.", ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bootstrap_ShouldInjectSDK_And_ExecuteScript()
    {
        if (_container == null)
        {
            throw new InvalidOperationException("Container verify failed initialization.");
        }

        // Arrange
        // We create a script that references the SDK and writes a file
        // Note: The SDK dll is mounted at /app/sdk
        // We need to verify where dotnet-script looks for assemblies or pass the path.
        // Usually #r needs a path.
        
        var scriptContent = $@"
#r ""{SdkPath}/Bendover.SDK.dll""
#r ""{SdkPath}/Bendover.Domain.dll"" 
using Bendover.SDK;

var fileSystem = new FileSystem();
fileSystem.Write(""/tmp/bootstrapped.txt"", ""Hello from Bendover!"");
";
        var scriptPath = "/tmp/bootstrap.csx";
        
        // Write script to container
        await _container.ExecAsync(new[] { "bash", "-c", $"echo '{scriptContent}' > {scriptPath}" });

        // Act
        // Make sure tools path is in PATH or use full path. dotnet tool install -g -> ~/.dotnet/tools
        // The default dotnet sdk image adds likely tools to path or we call via ~/.dotnet/tools/dotnet-script
        var execResult = await _container.ExecAsync(new[] { "bash", "-c", "export PATH=$PATH:/root/.dotnet/tools && dotnet script " + scriptPath });

        // Assert
        Assert.Equal(0, execResult.ExitCode);
        
        // Check if file exists
        var fileCheck = await _container.ExecAsync(new[] { "cat", "/tmp/bootstrapped.txt" });
        Assert.Contains("Hello from Bendover!", fileCheck.Stdout);
    }
}
