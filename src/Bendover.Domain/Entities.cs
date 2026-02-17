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

public enum AgenticStepActionKind
{
    Unknown,
    MutationWrite,
    MutationDelete,
    VerificationBuild,
    VerificationTest
}

public sealed record AgenticStepAction(
    AgenticStepActionKind Kind,
    string? Command = null)
{
    public bool IsMutationAction =>
        Kind is AgenticStepActionKind.MutationWrite or AgenticStepActionKind.MutationDelete;

    public bool IsVerificationAction =>
        Kind is AgenticStepActionKind.VerificationBuild or AgenticStepActionKind.VerificationTest;

    public string KindToken =>
        Kind switch
        {
            AgenticStepActionKind.MutationWrite => "mutation_write",
            AgenticStepActionKind.MutationDelete => "mutation_delete",
            AgenticStepActionKind.VerificationBuild => "verification_build",
            AgenticStepActionKind.VerificationTest => "verification_test",
            _ => "unknown"
        };
}

public sealed record ScriptExecutionResult(
    SandboxExecutionResult Execution,
    AgenticStepAction Action
);

public sealed record AgenticTurnSettings(
    string DiffCommand = "cd /workspace && git diff",
    string ChangedFilesCommand = "cd /workspace && git diff --name-only",
    string BuildCommand = "cd /workspace && dotnet build Bendover.sln",
    string TestCommand = "cd /workspace && dotnet test"
);

public sealed record AgenticTurnObservation(
    SandboxExecutionResult ScriptExecution,
    SandboxExecutionResult DiffExecution,
    SandboxExecutionResult ChangedFilesExecution,
    SandboxExecutionResult BuildExecution,
    string[] ChangedFiles,
    bool HasChanges,
    bool BuildPassed,
    AgenticStepAction Action
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
