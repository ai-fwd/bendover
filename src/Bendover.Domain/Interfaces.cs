using Bendover.Domain.Entities;

namespace Bendover.Domain.Interfaces;


public interface IContainerService
{
    Task StartContainerAsync(SandboxExecutionSettings settings);
    Task<ScriptExecutionResult> ExecuteScriptBodyAsync(string bodyContent);
    Task<SandboxExecutionResult> ExecuteCommandAsync(string command);
    Task<SandboxExecutionResult> ResetWorkspaceAsync(string baseCommit, bool cleanWorkspace = true);
    Task<SandboxExecutionResult> ApplyPatchAsync(string patchContent, bool checkOnly = false);
    Task StopContainerAsync();
}

public interface IAgentOrchestrator
{
    Task RunAsync(string initialGoal, IReadOnlyCollection<Practice> practices, string? agentsPath = null);
}
