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
        var completionSignaled = scriptResult.CompletionSignaled;

        if (scriptExecution.ExitCode != 0)
        {
            return new AgenticTurnObservation(
                ScriptExecution: scriptExecution,
                DiffExecution: SkippedResult("diff skipped because script execution failed"),
                HasChanges: false,
                CompletionSignaled: completionSignaled,
                StepPlan: scriptResult.StepPlan,
                ToolCall: scriptResult.ToolCall);
        }

        var diffExecution = completionSignaled
            ? await _containerService.ExecuteCommandAsync(turnSettings.DiffCommand)
            : SkippedResult("diff skipped until sdk.Done() is called");
        var hasChanges = completionSignaled && !string.IsNullOrWhiteSpace(diffExecution.CombinedOutput);

        return new AgenticTurnObservation(
            ScriptExecution: scriptExecution,
            DiffExecution: diffExecution,
            HasChanges: hasChanges,
            CompletionSignaled: completionSignaled,
            StepPlan: scriptResult.StepPlan,
            ToolCall: scriptResult.ToolCall);
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
