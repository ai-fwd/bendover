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

public enum AgentState
{
    Planning,
    Critiquing,
    Acting,
    Executing,
    Completed,
    Failed
}
