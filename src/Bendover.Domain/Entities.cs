namespace Bendover.Domain.Entities;

public record Plan(string Goal, List<string> Steps);
public record CritiqueResult(bool IsApproved, string Feedback);
public record ScriptContent(string Code);
public sealed record SandboxExecutionSettings(
    string SourceRepositoryPath,
    string? BaseCommit = null,
    bool CleanWorkspace = false
);
public sealed record SandboxExecutionResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    string CombinedOutput
);

public sealed record AgenticTurnSettings(
    string DiffCommand = "cd /workspace && git diff",
    string ChangedFilesCommand = "cd /workspace && git diff --name-only",
    string BuildCommand = "cd /workspace && dotnet build Bendover.sln"
);

public sealed record AgenticTurnObservation(
    SandboxExecutionResult ScriptExecution,
    SandboxExecutionResult DiffExecution,
    SandboxExecutionResult ChangedFilesExecution,
    SandboxExecutionResult BuildExecution,
    string[] ChangedFiles,
    bool HasChanges,
    bool BuildPassed
);

public enum AgentState
{
    Planning,
    Critiquing,
    Acting,
    Executing,
    Completed,
    Failed
}
