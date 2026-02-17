using Bendover.Application.Interfaces;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using System.Text.RegularExpressions;

namespace Bendover.Infrastructure.Services;

public class AgenticTurnService : IAgenticTurnService
{
    private readonly IContainerService _containerService;
    private static readonly Regex WriteActionRegex = new(
        @"\b(?:sdk\s*\.\s*File\s*\.\s*Write|sdk\s*\.\s*WriteFile)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DeleteActionRegex = new(
        @"\b(?:sdk\s*\.\s*File\s*\.\s*Delete|sdk\s*\.\s*DeleteFile)\s*\(",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ShellInvocationRegex = new(
        @"\b(?:sdk\s*\.\s*Shell\s*\.\s*Execute|sdk\s*\.\s*Run)\s*\(\s*""(?<cmd>(?:\\.|[^""\\])*)""\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex VerificationCommandRegex = new(
        @"^\s*(?:cd\s+\S+\s*&&\s*)?dotnet\s+(?<verb>build|test)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public AgenticTurnService(IContainerService containerService)
    {
        _containerService = containerService;
    }

    public async Task<AgenticTurnObservation> ExecuteAgenticTurnAsync(string scriptBody, AgenticTurnSettings settings)
    {
        var turnSettings = settings ?? new AgenticTurnSettings();
        var action = ClassifyAction(scriptBody);

        var scriptExecution = await _containerService.ExecuteScriptBodyAsync(scriptBody);
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
                ActionKind: action.Kind,
                ActionCommand: action.Command,
                IsVerificationAction: action.IsVerification,
                IsMutationAction: action.IsMutation);
        }

        var diffExecution = await _containerService.ExecuteCommandAsync(turnSettings.DiffCommand);
        var changedFilesExecution = await _containerService.ExecuteCommandAsync(turnSettings.ChangedFilesCommand);

        SandboxExecutionResult buildExecution;
        if (action.VerificationType == VerificationActionType.Build)
        {
            buildExecution = await _containerService.ExecuteCommandAsync(turnSettings.BuildCommand);
        }
        else if (action.VerificationType == VerificationActionType.Test)
        {
            buildExecution = await _containerService.ExecuteCommandAsync(turnSettings.TestCommand);
        }
        else
        {
            buildExecution = SkippedResult("verification command not requested by this step");
        }

        var changedFiles = ParseChangedFiles(changedFilesExecution.CombinedOutput);
        var hasChanges = !string.IsNullOrWhiteSpace(diffExecution.CombinedOutput);
        var buildPassed = action.IsVerification && buildExecution.ExitCode == 0;

        return new AgenticTurnObservation(
            ScriptExecution: scriptExecution,
            DiffExecution: diffExecution,
            ChangedFilesExecution: changedFilesExecution,
            BuildExecution: buildExecution,
            ChangedFiles: changedFiles,
            HasChanges: hasChanges,
            BuildPassed: buildPassed,
            ActionKind: action.Kind,
            ActionCommand: action.Command,
            IsVerificationAction: action.IsVerification,
            IsMutationAction: action.IsMutation);
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

    private static StepAction ClassifyAction(string scriptBody)
    {
        if (string.IsNullOrWhiteSpace(scriptBody))
        {
            return new StepAction("unknown", null, IsVerification: false, IsMutation: false, VerificationActionType.None);
        }

        var candidates = new List<(int Index, StepAction Action)>();

        var writeMatch = WriteActionRegex.Match(scriptBody);
        if (writeMatch.Success)
        {
            candidates.Add((writeMatch.Index, new StepAction("mutation_write", "sdk.File.Write", IsVerification: false, IsMutation: true, VerificationActionType.None)));
        }

        var deleteMatch = DeleteActionRegex.Match(scriptBody);
        if (deleteMatch.Success)
        {
            candidates.Add((deleteMatch.Index, new StepAction("mutation_delete", "sdk.File.Delete", IsVerification: false, IsMutation: true, VerificationActionType.None)));
        }

        foreach (Match match in ShellInvocationRegex.Matches(scriptBody))
        {
            var command = Regex.Unescape(match.Groups["cmd"].Value);
            var verificationType = ParseVerificationType(command);
            if (verificationType == VerificationActionType.None)
            {
                continue;
            }

            var kind = verificationType == VerificationActionType.Build
                ? "verification_build"
                : "verification_test";
            candidates.Add((match.Index, new StepAction(kind, command, IsVerification: true, IsMutation: false, verificationType)));
        }

        if (candidates.Count == 0)
        {
            return new StepAction("unknown", null, IsVerification: false, IsMutation: false, VerificationActionType.None);
        }

        return candidates
            .OrderBy(x => x.Index)
            .Select(x => x.Action)
            .First();
    }

    private static VerificationActionType ParseVerificationType(string command)
    {
        var match = VerificationCommandRegex.Match(command ?? string.Empty);
        if (!match.Success)
        {
            return VerificationActionType.None;
        }

        var verb = match.Groups["verb"].Value;
        if (string.Equals(verb, "build", StringComparison.OrdinalIgnoreCase))
        {
            return VerificationActionType.Build;
        }

        if (string.Equals(verb, "test", StringComparison.OrdinalIgnoreCase))
        {
            return VerificationActionType.Test;
        }

        return VerificationActionType.None;
    }

    private static SandboxExecutionResult SkippedResult(string reason)
    {
        return new SandboxExecutionResult(
            ExitCode: -1,
            Stdout: string.Empty,
            Stderr: string.Empty,
            CombinedOutput: $"skipped: {reason}");
    }

    private readonly record struct StepAction(
        string Kind,
        string? Command,
        bool IsVerification,
        bool IsMutation,
        VerificationActionType VerificationType);

    private enum VerificationActionType
    {
        None,
        Build,
        Test
    }
}
