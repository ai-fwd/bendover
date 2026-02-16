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

        var scriptExecution = await _containerService.ExecuteScriptBodyAsync(scriptBody);
        var diffExecution = await _containerService.ExecuteCommandAsync(turnSettings.DiffCommand);
        var changedFilesExecution = await _containerService.ExecuteCommandAsync(turnSettings.ChangedFilesCommand);
        var buildExecution = await _containerService.ExecuteCommandAsync(turnSettings.BuildCommand);

        var changedFiles = ParseChangedFiles(changedFilesExecution.CombinedOutput);
        var hasChanges = !string.IsNullOrWhiteSpace(diffExecution.CombinedOutput);
        var buildPassed = buildExecution.ExitCode == 0;

        return new AgenticTurnObservation(
            ScriptExecution: scriptExecution,
            DiffExecution: diffExecution,
            ChangedFilesExecution: changedFilesExecution,
            BuildExecution: buildExecution,
            ChangedFiles: changedFiles,
            HasChanges: hasChanges,
            BuildPassed: buildPassed);
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
}
