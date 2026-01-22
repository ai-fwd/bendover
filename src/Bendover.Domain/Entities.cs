namespace Bendover.Domain.Entities;

public record Plan(string Goal, List<string> Steps);
public record CritiqueResult(bool IsApproved, string Feedback);
public record ScriptContent(string Code);

public enum AgentState
{
    Planning,
    Critiquing,
    Acting,
    Executing,
    Completed,
    Failed
}
