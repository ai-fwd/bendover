using System;
using System.Collections.Generic;
using System.Linq;

namespace Bendover.Application.Evaluation;

public class EvaluatorEngine
{
    private readonly IEnumerable<IEvaluatorRule> _rules;

    public EvaluatorEngine(IEnumerable<IEvaluatorRule> rules)
    {
        _rules = rules;
    }

    public FinalEvaluation Evaluate(EvaluationContext context)
    {
        float score = 1.0f;
        var notes = new List<string>();
        var flags = new List<string>();
        var offendingPractices = new HashSet<string>();
        var notesByPractice = new Dictionary<string, List<string>>();
        bool hardFailure = false;

        foreach (var rule in _rules)
        {
            var result = rule.Evaluate(context);

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

            if (result.AffectedPractices != null && !result.Passed)
            {
                foreach (var practice in result.AffectedPractices)
                {
                    offendingPractices.Add(practice);
                }
            }

            if (result.NotesByPractice != null)
            {
                foreach (var entry in result.NotesByPractice)
                {
                    if (!notesByPractice.TryGetValue(entry.Key, out var list))
                    {
                        list = new List<string>();
                        notesByPractice[entry.Key] = list;
                    }

                    list.AddRange(entry.Value);
                }
            }
        }

        PracticeAttribution? attribution = null;
        if (offendingPractices.Count > 0 || notesByPractice.Count > 0)
        {
            attribution = new PracticeAttribution(
                SelectedPractices: Array.Empty<string>(),
                OffendingPractices: offendingPractices.ToArray(),
                NotesByPractice: notesByPractice.ToDictionary(k => k.Key, v => v.Value.ToArray())
            );
        }

        if (hardFailure)
        {
            return new FinalEvaluation(false, 0.0f, flags.ToArray(), notes.ToArray(), attribution);
        }

        // Clamp
        if (score < 0.0f) score = 0.0f;
        if (score > 1.0f) score = 1.0f;

        return new FinalEvaluation(true, score, flags.ToArray(), notes.ToArray(), attribution);
    }
}
