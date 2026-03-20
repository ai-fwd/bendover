using System.Text;
using Bendover.Domain.Entities;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Turn;

internal static class TurnContent
{
    public const int PromptHistoryDepth = 5;

    public static List<ChatMessage> BuildEngineerMessages(
        string engineerPromptTemplate,
        string practicesContext,
        string plan,
        string contextBlock)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System,
                $"{engineerPromptTemplate}\n\n" +
                $"Selected Practices:\n{practicesContext}"),
            new(ChatRole.User, $"Plan: {plan}")
        };

        if (!string.IsNullOrWhiteSpace(contextBlock))
        {
            messages.Add(new ChatMessage(ChatRole.User, contextBlock));
        }

        return messages;
    }

    public static string BuildContextBlock(IReadOnlyList<TurnHistoryEntry> history)
    {
        if (history.Count == 0)
        {
            return string.Empty;
        }

        var historyBuilder = new StringBuilder();
        historyBuilder.AppendLine($"Recent step history (oldest to newest, last {PromptHistoryDepth}):");
        foreach (var entry in history)
        {
            historyBuilder.AppendLine($"Step {entry.StepNumber} observation (raw):");
            historyBuilder.AppendLine(entry.ObservationContext);
            if (!string.IsNullOrWhiteSpace(entry.FailureContext))
            {
                historyBuilder.AppendLine($"Step {entry.StepNumber} failure (raw):");
                historyBuilder.AppendLine(entry.FailureContext);
            }

            historyBuilder.AppendLine();
        }

        return historyBuilder.ToString().TrimEnd();
    }

    public static void AppendStepHistory(
        List<TurnHistoryEntry> stepHistory,
        int stepNumber,
        string observationContext,
        string? failureContext)
    {
        stepHistory.Add(new TurnHistoryEntry(stepNumber, observationContext, failureContext));
        var overflow = stepHistory.Count - PromptHistoryDepth;
        if (overflow > 0)
        {
            stepHistory.RemoveRange(0, overflow);
        }
    }

    public static string BuildExceptionObservationContext(Exception exception)
    {
        return $"exception_type={exception.GetType().FullName ?? "n/a"}\n" +
               $"exception_message={exception.Message}\n" +
               $"exception:\n{exception}";
    }

    public static string BuildTurnFailureDigest(
        AgenticTurnObservation observation,
        IReadOnlyCollection<string> failedChecks)
    {
        var gateSummary = failedChecks.Count == 0
            ? "(none)"
            : string.Join(", ", failedChecks);
        var scriptTail = GetLastLines(observation.ScriptExecution.CombinedOutput, 40);
        var diffTail = GetLastLines(observation.DiffExecution.CombinedOutput, 40);

        return $"failed_checks={gateSummary}\n" +
               $"completion_signaled={ToBoolLiteral(observation.CompletionSignaled)}\n" +
               $"tool_call={observation.ToolCall ?? "(none)"}\n" +
               $"script_exit_code={observation.ScriptExecution.ExitCode}\n" +
               $"diff_exit_code={observation.DiffExecution.ExitCode}\n" +
               $"script_output_tail:\n{scriptTail}\n\n" +
               $"diff_output_tail:\n{diffTail}";
    }

    public static string BuildFailureDigest(int exitCode, Exception? exception, string combinedOutput)
    {
        var exceptionType = exception?.GetType().FullName ?? "n/a";
        var exceptionMessage = exception?.Message ?? "n/a";
        var tail = GetLastLines(combinedOutput, 40);
        return $"exit_code={exitCode}\nexception_type={exceptionType}\nexception_message={exceptionMessage}\noutput_tail:\n{tail}";
    }

    public static string BuildStepTool(AgenticTurnObservation observation)
    {
        if (!string.IsNullOrWhiteSpace(observation.ToolCall))
        {
            return observation.ToolCall!;
        }

        if (observation.CompletionSignaled)
        {
            return "sdk.Done()";
        }

        return "(none)";
    }

    public static string BuildStepObservationSummary(AgenticTurnObservation observation)
    {
        return $"completion_signaled={ToBoolLiteral(observation.CompletionSignaled)}; " +
               $"script_exit={observation.ScriptExecution.ExitCode}; " +
               $"has_changes={observation.HasChanges}; " +
               $"diff_exit={observation.DiffExecution.ExitCode}; " +
               $"is_done={ToBoolLiteral(observation.CompletionSignaled)}";
    }

    private static string ToBoolLiteral(bool value)
    {
        return value ? "true" : "false";
    }

    private static string GetLastLines(string text, int maxLines)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);

        if (lines.Length <= maxLines)
        {
            return text;
        }

        return string.Join('\n', lines.Skip(lines.Length - maxLines));
    }
}
