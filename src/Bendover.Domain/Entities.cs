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

public sealed record ScriptExecutionResult(
    SandboxExecutionResult Execution,
    bool CompletionSignaled,
    string? StepPlan = null,
    string? ToolCall = null
);

public sealed record AgenticTurnSettings(
    string DiffCommand = "cd /workspace && git diff"
);

public sealed record AgenticTurnObservation(
    SandboxExecutionResult ScriptExecution,
    SandboxExecutionResult DiffExecution,
    bool HasChanges,
    bool CompletionSignaled,
    string? StepPlan = null,
    string? ToolCall = null
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
