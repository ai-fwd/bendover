using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using System.Diagnostics;
using System.Net.Sockets;
using Xunit;

namespace Bendover.Tests;

public class ContainerIntegrationTest : IAsyncLifetime
{
    private IContainer? _container;
    private const string SdkPath = "/app/sdk";
    private const string DomainPath = "/app/domain";

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

        var repoRoot = FindRepoRoot();
        var configuration = ResolveConfiguration();
        var localSdkPath = EnsureProjectOutput(repoRoot, "Bendover.SDK", configuration);
        var localDomainPath = EnsureProjectOutput(repoRoot, "Bendover.Domain", configuration);
        
        try 
        {
            // Suppress obsolescence warning for now as API exploration is limited without docs
            #pragma warning disable CS0618 
            _container = new ContainerBuilder()
                .WithImage("mcr.microsoft.com/dotnet/sdk:10.0")
                .WithCommand("sleep", "infinity")
                .WithBindMount(localSdkPath, SdkPath)
                .WithBindMount(localDomainPath, DomainPath)
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
#r ""{DomainPath}/Bendover.Domain.dll"" 
using Bendover.SDK;

var fileSystem = new BendoverSDK().File;
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
        Assert.True(execResult.ExitCode == 0,
            $"exit={execResult.ExitCode}\n--- stdout ---\n{execResult.Stdout}\n--- stderr ---\n{execResult.Stderr}");
        
        // Check if file exists
        var fileCheck = await _container.ExecAsync(new[] { "cat", "/tmp/bootstrapped.txt" });
        Assert.Contains("Hello from Bendover!", fileCheck.Stdout);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bendover.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Bendover.sln from test base directory.");
    }

    private static string ResolveConfiguration()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var releaseToken = $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}";
        var debugToken = $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}";

        if (baseDirectory.Contains(releaseToken, StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        if (baseDirectory.Contains(debugToken, StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        return "Debug";
    }

    private static string EnsureProjectOutput(string repoRoot, string projectName, string configuration)
    {
        var projectDirectory = Path.Combine(repoRoot, "src", projectName);
        var projectPath = Path.Combine(projectDirectory, $"{projectName}.csproj");
        var outputDirectory = Path.Combine(projectDirectory, "bin", configuration, "net10.0");
        var outputDll = Path.Combine(outputDirectory, $"{projectName}.dll");

        if (!File.Exists(outputDll))
        {
            RunProcess("dotnet", $"build \"{projectPath}\" -c {configuration}", repoRoot);
        }

        if (!File.Exists(outputDll))
        {
            throw new InvalidOperationException($"Build output not found: {outputDll}");
        }

        return outputDirectory;
    }

    private static void RunProcess(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command failed: {fileName} {arguments}\n{stdout}\n{stderr}");
        }
    }
}
