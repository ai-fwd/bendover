using System.Text;
using System.Text.Json;
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

        var buildRunnerResult = await ExecuteCommandAsync($"cd {WorkspacePath} && dotnet build src/Bendover.ScriptRunner/Bendover.ScriptRunner.csproj -c Debug -v minimal");
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
                Action: new AgenticStepAction(AgenticStepActionKind.Unknown),
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
            $"cd {WorkspacePath} && dotnet src/Bendover.ScriptRunner/bin/Debug/net10.0/Bendover.ScriptRunner.dll --body-file {ScriptBodyPath} --result-file {scriptResultPath}");

        var (action, stepPlan, toolCall, warning) = await TryReadScriptActionAsync(scriptResultPath);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            executeResult = AppendCombinedOutput(executeResult, warning);
        }

        return new ScriptExecutionResult(
            Execution: executeResult,
            Action: action,
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

        var patchFile = $"/tmp/bendover_patch_{Guid.NewGuid():N}.patch";
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

    private async Task<(AgenticStepAction Action, string? StepPlan, string? ToolCall, string? Warning)> TryReadScriptActionAsync(string scriptResultPath)
    {
        SandboxExecutionResult readResult;
        try
        {
            readResult = await ExecuteCommandAsync($"cat '{scriptResultPath}'");
        }
        catch (Exception ex)
        {
            return (
                new AgenticStepAction(AgenticStepActionKind.Unknown),
                null,
                null,
                $"script action metadata missing: {ex.Message}");
        }

        if (readResult.ExitCode != 0 || string.IsNullOrWhiteSpace(readResult.CombinedOutput))
        {
            return (
                new AgenticStepAction(AgenticStepActionKind.Unknown),
                null,
                null,
                $"script action metadata unavailable.\n{readResult.CombinedOutput}");
        }

        try
        {
            var dto = JsonSerializer.Deserialize<ScriptRunnerActionDto>(
                readResult.CombinedOutput,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null || string.IsNullOrWhiteSpace(dto.kind))
            {
                return (
                    new AgenticStepAction(AgenticStepActionKind.Unknown),
                    null,
                    null,
                    $"script action metadata invalid.\n{readResult.CombinedOutput}");
            }

            var kind = dto.kind.Trim().ToLowerInvariant() switch
            {
                "mutation_write" => AgenticStepActionKind.MutationWrite,
                "mutation_delete" => AgenticStepActionKind.MutationDelete,
                "verification_build" => AgenticStepActionKind.VerificationBuild,
                "verification_test" => AgenticStepActionKind.VerificationTest,
                "discovery_shell" => AgenticStepActionKind.DiscoveryShell,
                "complete" => AgenticStepActionKind.Complete,
                _ => AgenticStepActionKind.Unknown
            };

            var warning = kind == AgenticStepActionKind.Unknown
                ? $"script action metadata kind '{dto.kind}' not recognized."
                : null;
            return (
                new AgenticStepAction(kind, dto.command),
                string.IsNullOrWhiteSpace(dto.step_plan) ? null : dto.step_plan.Trim(),
                string.IsNullOrWhiteSpace(dto.tool_call) ? null : dto.tool_call.Trim(),
                warning);
        }
        catch (Exception ex)
        {
            return (
                new AgenticStepAction(AgenticStepActionKind.Unknown),
                null,
                null,
                $"script action metadata parse failed: {ex.Message}\n{readResult.CombinedOutput}");
        }
    }

    private static SandboxExecutionResult AppendCombinedOutput(SandboxExecutionResult result, string text)
    {
        var suffix = string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : $"\n\n[script_action_metadata]\n{text}";

        return new SandboxExecutionResult(
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            $"{result.CombinedOutput}{suffix}");
    }

    private sealed record ScriptRunnerActionDto(
        string kind,
        string? command,
        string? step_plan,
        string? tool_call);
}
