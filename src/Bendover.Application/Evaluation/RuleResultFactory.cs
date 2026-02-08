using System.Collections.Generic;

namespace Bendover.Application.Evaluation;

public static class RuleResultFactory
{
    public static RuleResult CreateRuleResult(
        this IEvaluatorRule rule,
        bool passed,
        float scoreDelta,
        string[] notes,
        bool isHardFailure = false,
        string[]? affectedPractices = null,
        Dictionary<string, string[]>? notesByPractice = null)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new RuleResult(
            rule.GetType().Name,
            passed,
            scoreDelta,
            notes,
            IsHardFailure: isHardFailure,
            AffectedPractices: affectedPractices,
            NotesByPractice: notesByPractice);
    }
}
