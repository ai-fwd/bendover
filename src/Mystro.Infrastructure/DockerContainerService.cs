using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net;
using Mystro.Domain.Entities;
using Mystro.Domain.Interfaces;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Mystro.Infrastructure;

public class DockerContainerService : IContainerService
{
    private readonly DockerClient _client;
    private readonly IAgentEventPublisher _events;
    private string? _containerId;
    private const string SandboxImageTag = "mystro/sandbox:local";
    private const string InputRepoPath = "/input/repo";
    private const string WorkspacePath = "/workspace";
    private const string ScriptBodyPath = "/workspace/script_body.csx";

    public DockerContainerService(IAgentEventPublisher events)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));

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

        await EnsureSandboxImageAsync(sourceRepositoryPath);

        await _events.ProgressAsync($"Starting sandbox container from image '{SandboxImageTag}'...");
        var response = await _client.Containers.CreateContainerAsync(new CreateContainerParameters
        {
            Image = SandboxImageTag,
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
            var resetResult = await ResetWorkspaceAsync(settings.BaseCommit, cleanWorkspace: true);
            if (resetResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to reset workspace to base commit.\n{resetResult.CombinedOutput}");
            }
        }
        else if (settings.CleanWorkspace)
        {
            var cleanResult = await ExecuteCommandAsync($"cd {WorkspacePath} && git clean -fd");
            if (cleanResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to clean workspace.\n{cleanResult.CombinedOutput}");
            }
        }

        var buildRunnerResult = await ExecuteCommandAsync($"cd {WorkspacePath} && dotnet build src/Mystro.ScriptRunner/Mystro.ScriptRunner.csproj -c Debug -v minimal");
        if (buildRunnerResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to build ScriptRunner inside sandbox.\n{buildRunnerResult.CombinedOutput}");
        }
    }

    public async Task<ScriptExecutionResult> ExecuteScriptBodyAsync(string bodyContent)
    {
        EnsureContainerStarted();

        var encodedBody = Convert.ToBase64String(Encoding.UTF8.GetBytes(bodyContent));
        var writeBodyResult = await ExecuteCommandAsync($"printf '%s' '{encodedBody}' | base64 -d > {ScriptBodyPath}");
        if (writeBodyResult.ExitCode != 0)
        {
            return new ScriptExecutionResult(
                Execution: writeBodyResult,
                CompletionSignaled: false,
                StepPlan: null,
                ToolCall: null);
        }

        var scriptResultPath = $"{WorkspacePath}/script_result.json";
        try
        {
            await ExecuteCommandAsync($"rm -f '{scriptResultPath}'");
        }
        catch
        {
            // Best effort cleanup before each execution.
        }

        var executeResult = await ExecuteCommandAsync(
            $"cd {WorkspacePath} && dotnet src/Mystro.ScriptRunner/bin/Debug/net10.0/Mystro.ScriptRunner.dll --body-file {ScriptBodyPath} --result-file {scriptResultPath}");

        var (completionSignaled, stepPlan, toolCall, warning) = await TryReadScriptResultMetadataAsync(scriptResultPath);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            executeResult = AppendCombinedOutput(executeResult, warning);
        }

        return new ScriptExecutionResult(
            Execution: executeResult,
            CompletionSignaled: completionSignaled,
            StepPlan: stepPlan,
            ToolCall: toolCall);
    }

    public async Task<SandboxExecutionResult> ResetWorkspaceAsync(string baseCommit, bool cleanWorkspace = true)
    {
        EnsureContainerStarted();

        if (string.IsNullOrWhiteSpace(baseCommit))
        {
            return new SandboxExecutionResult(
                ExitCode: 1,
                Stdout: string.Empty,
                Stderr: "baseCommit cannot be empty.",
                CombinedOutput: "stage=reset\nbaseCommit cannot be empty.");
        }

        var escapedCommit = EscapeForSingleQuotedShell(baseCommit.Trim());
        var resetResult = await ExecuteCommandAsync($"cd {WorkspacePath} && git reset --hard '{escapedCommit}'");
        if (resetResult.ExitCode != 0)
        {
            return WithStage(resetResult, "reset");
        }

        if (!cleanWorkspace)
        {
            return WithStage(resetResult, "reset");
        }

        var cleanResult = await ExecuteCommandAsync($"cd {WorkspacePath} && git clean -fd");
        if (cleanResult.ExitCode != 0)
        {
            return new SandboxExecutionResult(
                ExitCode: cleanResult.ExitCode,
                Stdout: $"{resetResult.Stdout}{cleanResult.Stdout}",
                Stderr: $"{resetResult.Stderr}{cleanResult.Stderr}",
                CombinedOutput:
                    "stage=clean\n" +
                    $"reset_output:\n{resetResult.CombinedOutput}\n\n" +
                    $"clean_output:\n{cleanResult.CombinedOutput}");
        }

        return new SandboxExecutionResult(
            ExitCode: 0,
            Stdout: $"{resetResult.Stdout}{cleanResult.Stdout}",
            Stderr: $"{resetResult.Stderr}{cleanResult.Stderr}",
            CombinedOutput:
                "stage=reset_and_clean\n" +
                $"reset_output:\n{resetResult.CombinedOutput}\n\n" +
                $"clean_output:\n{cleanResult.CombinedOutput}");
    }

    public async Task<SandboxExecutionResult> ApplyPatchAsync(string patchContent, bool checkOnly = false)
    {
        EnsureContainerStarted();

        if (string.IsNullOrWhiteSpace(patchContent))
        {
            return new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty);
        }

        var patchFile = $"/tmp/mystro_patch_{Guid.NewGuid():N}.patch";
        try
        {
            var encodedPatch = Convert.ToBase64String(Encoding.UTF8.GetBytes(patchContent));
            var writeResult = await ExecuteCommandAsync($"printf '%s' '{encodedPatch}' | base64 -d > '{patchFile}'");
            if (writeResult.ExitCode != 0)
            {
                return WithStage(writeResult, "write_patch");
            }

            var command = checkOnly
                ? $"cd {WorkspacePath} && git apply --check '{patchFile}'"
                : $"cd {WorkspacePath} && git apply --whitespace=nowarn '{patchFile}'";
            var applyResult = await ExecuteCommandAsync(command);
            return applyResult.ExitCode == 0
                ? applyResult
                : WithStage(applyResult, checkOnly ? "apply_check" : "apply");
        }
        finally
        {
            try
            {
                await ExecuteCommandAsync($"rm -f '{patchFile}'");
            }
            catch
            {
                // Best effort cleanup for temp patch file.
            }
        }
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

    private static SandboxExecutionResult WithStage(SandboxExecutionResult result, string stage)
    {
        return new SandboxExecutionResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            $"stage={stage}\n{result.CombinedOutput}");
    }

    private static string EscapeForSingleQuotedShell(string value)
    {
        return value.Replace("'", "'\"'\"'", StringComparison.Ordinal);
    }

    private async Task<(bool CompletionSignaled, string? StepPlan, string? ToolCall, string? Warning)> TryReadScriptResultMetadataAsync(string scriptResultPath)
    {
        SandboxExecutionResult readResult;
        try
        {
            readResult = await ExecuteCommandAsync($"cat '{scriptResultPath}'");
        }
        catch (Exception ex)
        {
            return (
                false,
                null,
                null,
                $"script result metadata missing: {ex.Message}");
        }

        if (readResult.ExitCode != 0 || string.IsNullOrWhiteSpace(readResult.CombinedOutput))
        {
            return (
                false,
                null,
                null,
                $"script result metadata unavailable.\n{readResult.CombinedOutput}");
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ScriptRunnerActionDto>(
                readResult.CombinedOutput,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null)
            {
                return (
                    false,
                    null,
                    null,
                    $"script result metadata invalid.\n{readResult.CombinedOutput}");
            }

            return (
                dto.is_done,
                string.IsNullOrWhiteSpace(dto.step_plan) ? null : dto.step_plan.Trim(),
                string.IsNullOrWhiteSpace(dto.tool_call) ? null : dto.tool_call.Trim(),
                null);
        }
        catch (Exception ex)
        {
            return (
                false,
                null,
                null,
                $"script result metadata parse failed: {ex.Message}\n{readResult.CombinedOutput}");
        }
    }

    private static SandboxExecutionResult AppendCombinedOutput(SandboxExecutionResult result, string text)
    {
        var suffix = string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : $"\n\n[script_result_metadata]\n{text}";

        return new SandboxExecutionResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            $"{result.CombinedOutput}{suffix}");
    }

    private sealed record ScriptRunnerActionDto(
        bool is_done,
        string? step_plan,
        string? tool_call);

    private async Task EnsureSandboxImageAsync(string sourceRepositoryPath)
    {
        await _events.ProgressAsync($"Checking sandbox image cache ('{SandboxImageTag}')...");
        if (await SandboxImageExistsAsync())
        {
            await _events.ProgressAsync($"Sandbox image ready from local cache ('{SandboxImageTag}').");
            return;
        }

        var dockerfilePath = Path.Combine(sourceRepositoryPath, "Dockerfile");
        if (!File.Exists(dockerfilePath))
        {
            await _events.ProgressAsync($"Sandbox image bootstrap failed: Dockerfile not found at '{dockerfilePath}'.");
            throw new InvalidOperationException(
                $"Sandbox image '{SandboxImageTag}' is missing and no Dockerfile was found at '{dockerfilePath}'. " +
                "Ensure SourceRepositoryPath points to the repository root.");
        }

        await _events.ProgressAsync("Sandbox image missing; building from Dockerfile (first run may take a few minutes)...");
        HostCommandResult buildResult;
        try
        {
            buildResult = await RunHostCommandAsync(
                "docker",
                ["build", "-t", SandboxImageTag, "-f", dockerfilePath, sourceRepositoryPath],
                sourceRepositoryPath);
        }
        catch (Exception ex)
        {
            await _events.ProgressAsync($"Sandbox image bootstrap failed while invoking Docker CLI: {ex.GetType().Name}.");
            throw new InvalidOperationException(
                $"Failed to invoke Docker CLI while building sandbox image '{SandboxImageTag}'. " +
                "Ensure Docker CLI is installed and available on PATH.", ex);
        }

        if (buildResult.ExitCode != 0)
        {
            await _events.ProgressAsync($"Sandbox image build failed (exit code {buildResult.ExitCode}).");
            throw new InvalidOperationException(
                $"Failed to build sandbox image '{SandboxImageTag}' from '{dockerfilePath}'.\n" +
                $"Command: docker build -t {SandboxImageTag} -f \"{dockerfilePath}\" \"{sourceRepositoryPath}\"\n" +
                $"{buildResult.CombinedOutput}");
        }

        await _events.ProgressAsync($"Sandbox image build completed ('{SandboxImageTag}').");
        if (!await SandboxImageExistsAsync())
        {
            await _events.ProgressAsync($"Sandbox image bootstrap failed: image '{SandboxImageTag}' still not found after build.");
            throw new InvalidOperationException(
                $"Docker build completed but sandbox image '{SandboxImageTag}' is still not available locally.");
        }
    }

    private async Task<bool> SandboxImageExistsAsync()
    {
        try
        {
            await _client.Images.InspectImageAsync(SandboxImageTag);
            return true;
        }
        catch (DockerApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<HostCommandResult> RunHostCommandAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new HostCommandResult(process.ExitCode, stdout, stderr);
    }

    private sealed record HostCommandResult(int ExitCode, string Stdout, string Stderr)
    {
        public string CombinedOutput => $"{Stdout}{Stderr}";
    }
}
