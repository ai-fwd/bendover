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

public interface IBendoverSDK
{
    // The Actor codes against this contract.
    // In the real implementation (SDK project), this will expose File, Git, Shell, Signal capabilities.
    // For the Domain, we define the contracts that the Actor *assumes* exist.
    
    IFileSystem File { get; }
    IGit Git { get; }
    IShell Shell { get; }
    ISignal Signal { get; }
}

public interface IFileSystem
{
    void Write(string path, string content);
    string Read(string path);
    bool Exists(string path);
    void Delete(string path);
}

public interface IGit
{
    void Clone(string url);
    void Commit(string message);
    void Push();
}

public interface IShell
{
    string Execute(string command);
}

public interface ISignal
{
    void Done();
}
