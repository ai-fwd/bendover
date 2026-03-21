using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;

namespace Bendover.Application.Run.Stages;

public sealed class RepositoryStage : RunStage
{
    private readonly IContainerService _containerService;
    private readonly IGitRunner _gitRunner;
    private readonly IPromptOptRunRecorder _runRecorder;

    public RepositoryStage(
        IContainerService containerService,
        IGitRunner gitRunner,
        IPromptOptRunRecorder runRecorder)
    {
        _containerService = containerService;
        _gitRunner = gitRunner;
        _runRecorder = runRecorder;
    }

    public override int SetupOrder => 10;

    public override int TeardownOrder => 30;

    public override async Task SetupAsync(RunStageContext context)
    {
        try
        {
            context.BaseCommit = (await _gitRunner.RunAsync("rev-parse HEAD")).Trim();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to resolve base commit from git HEAD.", ex);
        }

        if (string.IsNullOrWhiteSpace(context.BaseCommit))
        {
            throw new InvalidOperationException("Failed to resolve base commit from git HEAD: command returned an empty commit hash.");
        }
    }

    public override async Task TeardownAsync(RunStageContext context)
    {
        if (context.RunResult?.Kind != RunResultKind.Completed)
        {
            return;
        }

        context.GitDiffContent = await PersistArtifactFromSandboxCommandAsync(
            "cd /workspace && git diff",
            "git_diff.patch",
            "git_diff.patch");

        await ApplySandboxPatchToSourceAsync(context);
    }

    private async Task<string> PersistArtifactFromSandboxCommandAsync(string command, string successArtifactName, string errorArtifactName)
    {
        try
        {
            var result = await _containerService.ExecuteCommandAsync(command);
            var targetArtifact = result.ExitCode == 0 ? successArtifactName : errorArtifactName;
            await _runRecorder.RecordArtifactAsync(targetArtifact, result.CombinedOutput);
            return result.CombinedOutput;
        }
        catch (Exception ex)
        {
            await _runRecorder.RecordArtifactAsync(errorArtifactName, ex.ToString());
            return string.Empty;
        }
    }

    private async Task ApplySandboxPatchToSourceAsync(RunStageContext context)
    {
        if (!context.PromptOptRunContext.ApplySandboxPatchToSource)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(context.GitDiffContent))
        {
            return;
        }

        try
        {
            var checkOutput = await _gitRunner.RunAsync(
                "apply --check --whitespace=nowarn -",
                standardInput: context.GitDiffContent);
            await _runRecorder.RecordArtifactAsync(
                "host_apply_check.txt",
                string.IsNullOrWhiteSpace(checkOutput) ? "(no output)" : checkOutput);
        }
        catch (Exception ex)
        {
            await _runRecorder.RecordArtifactAsync("host_apply_check.txt", ex.ToString());
            throw new InvalidOperationException(
                $"Host patch apply failed at check stage.\n{ex.Message}",
                ex);
        }

        try
        {
            var applyOutput = await _gitRunner.RunAsync(
                "apply --whitespace=nowarn -",
                standardInput: context.GitDiffContent);
            await _runRecorder.RecordArtifactAsync(
                "host_apply_result.txt",
                string.IsNullOrWhiteSpace(applyOutput) ? "(no output)" : applyOutput);
        }
        catch (Exception ex)
        {
            await _runRecorder.RecordArtifactAsync("host_apply_result.txt", ex.ToString());
            throw new InvalidOperationException(
                $"Host patch apply failed at apply stage.\n{ex.Message}",
                ex);
        }
    }
}
