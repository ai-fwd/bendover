using Bendover.Application.Interfaces;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;

namespace Bendover.Infrastructure.Services;

public class AgenticTurnService : IAgenticTurnService
{
    private readonly IContainerService _containerService;

    public AgenticTurnService(IContainerService containerService)
    {
        _containerService = containerService;
    }

    public async Task<AgenticTurnObservation> ExecuteAgenticTurnAsync(string scriptBody, AgenticTurnSettings settings)
    {
        var turnSettings = settings ?? new AgenticTurnSettings();

        var scriptResult = await _containerService.ExecuteScriptBodyAsync(scriptBody);
        var scriptExecution = scriptResult.Execution;
        var action = scriptResult.Action;

        if (scriptExecution.ExitCode != 0)
        {
            return new AgenticTurnObservation(
                ScriptExecution: scriptExecution,
                DiffExecution: SkippedResult("diff skipped because script execution failed"),
                ChangedFilesExecution: SkippedResult("changed-files skipped because script execution failed"),
                BuildExecution: SkippedResult("verification skipped because script execution failed"),
                ChangedFiles: Array.Empty<string>(),
                HasChanges: false,
                BuildPassed: false,
                Action: action);
        }

        var diffExecution = await _containerService.ExecuteCommandAsync(turnSettings.DiffCommand);
        var changedFilesExecution = await _containerService.ExecuteCommandAsync(turnSettings.ChangedFilesCommand);

        SandboxExecutionResult buildExecution;
        if (action.Kind == AgenticStepActionKind.VerificationBuild)
        {
            buildExecution = await _containerService.ExecuteCommandAsync(turnSettings.BuildCommand);
        }
        else if (action.Kind == AgenticStepActionKind.VerificationTest)
        {
            buildExecution = await _containerService.ExecuteCommandAsync(turnSettings.TestCommand);
        }
        else
        {
            buildExecution = SkippedResult("verification command not requested by this step");
        }

        var changedFiles = ParseChangedFiles(changedFilesExecution.CombinedOutput);
        var hasChanges = !string.IsNullOrWhiteSpace(diffExecution.CombinedOutput);
        var buildPassed = action.IsVerificationAction && buildExecution.ExitCode == 0;

        return new AgenticTurnObservation(
            ScriptExecution: scriptExecution,
            DiffExecution: diffExecution,
            ChangedFilesExecution: changedFilesExecution,
            BuildExecution: buildExecution,
            ChangedFiles: changedFiles,
            HasChanges: hasChanges,
            BuildPassed: buildPassed,
            Action: action);
    }

    private static string[] ParseChangedFiles(string output)
    {
        return output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static SandboxExecutionResult SkippedResult(string reason)
    {
        return new SandboxExecutionResult(
            ExitCode: -1,
            Stdout: string.Empty,
            Stderr: string.Empty,
            CombinedOutput: $"skipped: {reason}");
    }
}
