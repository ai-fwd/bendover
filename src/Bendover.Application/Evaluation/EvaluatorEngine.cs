using System;
using System.Collections.Generic;
using System.Linq;

namespace Bendover.Application.Evaluation;

public class EvaluatorEngine
{
    private readonly IEnumerable<IEvaluatorRule> _rules;
    private readonly PracticeAttributionBuilder _attributionBuilder;
    private readonly RulePracticeConventionMatcher _matcher;

    public EvaluatorEngine(
        IEnumerable<IEvaluatorRule> rules,
        PracticeAttributionBuilder? attributionBuilder = null,
        RulePracticeConventionMatcher? matcher = null)
    {
        _rules = rules;
        _matcher = matcher ?? new RulePracticeConventionMatcher();
        _attributionBuilder = attributionBuilder ?? new PracticeAttributionBuilder(_matcher);
    }

    public FinalEvaluation Evaluate(EvaluationContext context)
    {
        float score = 1.0f;
        var notes = new List<string>();
        var flags = new List<string>();
        bool hardFailure = false;
        var results = new List<RuleResult>();
        var selectedPractices = context.SelectedPractices ?? Array.Empty<string>();
        var allPractices = context.AllPractices ?? Array.Empty<string>();

        foreach (var rule in _rules)
        {
            if (!ShouldRun(rule, selectedPractices, allPractices))
            {
                continue;
            }

            var result = rule.Evaluate(context);
            results.Add(result);

            if (result.IsHardFailure || !result.Passed)
            {
                if (!result.Passed)
                {
                    if (result.IsHardFailure)
                    {
                        hardFailure = true;
                        flags.Add(result.RuleName);
                    }

                    // Even if soft fail, apply penalty
                    score += result.ScoreDelta; // Delta is typically negative for penalty
                }
                else
                {
                    // Even if passed, maybe score bonus? Usually 0 change.
                    score += result.ScoreDelta;
                }
            }
            else // Passed
            {
                score += result.ScoreDelta;
            }

            if (result.Notes != null)
            {
                notes.AddRange(result.Notes);
            }
        }

        var attribution = _attributionBuilder.Build(results, selectedPractices);

        if (hardFailure)
        {
            return new FinalEvaluation(false, 0.0f, flags.ToArray(), notes.ToArray(), attribution);
        }

        // Clamp
        if (score < 0.0f) score = 0.0f;
        if (score > 1.0f) score = 1.0f;

        return new FinalEvaluation(true, score, flags.ToArray(), notes.ToArray(), attribution);
    }

    private bool ShouldRun(IEvaluatorRule rule, IReadOnlyList<string> selectedPractices, IReadOnlyList<string> allPractices)
    {
        var ruleName = rule.GetType().Name;
        var matchesAnyPractice = _matcher.Match(ruleName, allPractices);
        if (matchesAnyPractice.Count == 0)
        {
            // No matching practice by convention: treat as global rule.
            return true;
        }

        var matchesSelected = _matcher.Match(ruleName, selectedPractices);
        return matchesSelected.Count > 0;
    }
}
