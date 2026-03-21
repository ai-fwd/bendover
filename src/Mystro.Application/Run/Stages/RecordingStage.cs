using System.Text.Json;
using Mystro.Application.Interfaces;

namespace Mystro.Application.Run.Stages;

public sealed class RecordingStage : RunStage
{
    private readonly IPromptOptRunRecorder _runRecorder;
    private bool _started;

    public RecordingStage(IPromptOptRunRecorder runRecorder)
    {
        _runRecorder = runRecorder;
    }

    public override int SetupOrder => 20;

    public override int TeardownOrder => 10;

    public override async Task SetupAsync(RunStageContext context)
    {
        await _runRecorder.StartRunAsync(context.InitialGoal, context.BaseCommit, context.BundleId);
        _started = true;
    }

    public override async Task TeardownAsync(RunStageContext context)
    {
        if (!_started)
        {
            return;
        }

        context.RunResult ??= RunResult.FailedException(
            lastScriptExitCode: null,
            lastFailureDigest: context.TerminalException?.ToString());

        var runResultArtifact = new
        {
            status = GetStatus(context.RunResult.Kind),
            completion_step = context.RunResult.CompletionStep,
            completion_signaled = context.RunResult.CompletionSignaled,
            has_code_changes = context.RunResult.HasCodeChanges,
            git_diff_bytes = context.RunResult.GitDiffBytes,
            last_script_exit_code = context.RunResult.LastScriptExitCode,
            last_failure_digest = context.RunResult.LastFailureDigest
        };

        await _runRecorder.RecordArtifactAsync(
            "run_result.json",
            JsonSerializer.Serialize(runResultArtifact));

        await _runRecorder.FinalizeRunAsync();
    }

    private static string GetStatus(RunResultKind kind)
    {
        return kind switch
        {
            RunResultKind.Completed => "completed",
            RunResultKind.FailedException => "failed_exception",
            RunResultKind.FailedMaxTurns => "failed_max_turns",
            _ => throw new InvalidOperationException($"Unsupported run result kind: {kind}")
        };
    }
}
