namespace Mystro.Application.Run;

public sealed record RunResult(
    RunResultKind Kind,
    int? CompletionStep,
    bool CompletionSignaled,
    bool HasCodeChanges,
    int GitDiffBytes,
    int? LastScriptExitCode,
    string? LastFailureDigest)
{
    public static RunResult Completed(
        int completionStep,
        bool completionSignaled,
        bool hasCodeChanges,
        int gitDiffBytes,
        int? lastScriptExitCode,
        string? lastFailureDigest)
    {
        return new RunResult(
            RunResultKind.Completed,
            completionStep,
            completionSignaled,
            hasCodeChanges,
            gitDiffBytes,
            lastScriptExitCode,
            lastFailureDigest);
    }

    public static RunResult FailedException(
        int? lastScriptExitCode,
        string? lastFailureDigest)
    {
        return new RunResult(
            RunResultKind.FailedException,
            CompletionStep: null,
            CompletionSignaled: false,
            HasCodeChanges: false,
            GitDiffBytes: 0,
            LastScriptExitCode: lastScriptExitCode,
            LastFailureDigest: lastFailureDigest);
    }

    public static RunResult FailedMaxTurns(
        int? lastScriptExitCode,
        string? lastFailureDigest)
    {
        return new RunResult(
            RunResultKind.FailedMaxTurns,
            CompletionStep: null,
            CompletionSignaled: false,
            HasCodeChanges: false,
            GitDiffBytes: 0,
            LastScriptExitCode: lastScriptExitCode,
            LastFailureDigest: lastFailureDigest);
    }
}

public enum RunResultKind
{
    Completed,
    FailedException,
    FailedMaxTurns
}
