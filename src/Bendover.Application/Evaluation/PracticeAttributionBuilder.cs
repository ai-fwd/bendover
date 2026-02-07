namespace Bendover.Application.Evaluation;

public class PracticeAttributionBuilder
{
    private readonly RulePracticeConventionMatcher _matcher;

    public PracticeAttributionBuilder(RulePracticeConventionMatcher matcher)
    {
        _matcher = matcher;
    }

    public PracticeAttribution Build(IEnumerable<RuleResult> ruleResults, IEnumerable<string>? selectedPractices)
    {
        var selected = selectedPractices?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        var offenders = new List<string>();
        var offenderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var notesByPractice = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in ruleResults)
        {
            if (result.Passed)
            {
                continue;
            }

            var resolvedPractices = ResolvePractices(result, selected);
            foreach (var practice in resolvedPractices)
            {
                if (offenderSet.Add(practice))
                {
                    offenders.Add(practice);
                }
            }

            if (resolvedPractices.Count == 0)
            {
                continue;
            }

            if (result.NotesByPractice != null && result.NotesByPractice.Count > 0)
            {
                foreach (var entry in result.NotesByPractice)
                {
                    if (!resolvedPractices.Contains(entry.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!notesByPractice.TryGetValue(entry.Key, out var list))
                    {
                        list = new List<string>();
                        notesByPractice[entry.Key] = list;
                    }

                    list.AddRange(entry.Value);
                }

                continue;
            }

            if (result.Notes.Length == 0)
            {
                continue;
            }

            foreach (var practice in resolvedPractices)
            {
                if (!notesByPractice.TryGetValue(practice, out var list))
                {
                    list = new List<string>();
                    notesByPractice[practice] = list;
                }

                list.AddRange(result.Notes);
            }
        }

        return new PracticeAttribution(
            SelectedPractices: selected,
            OffendingPractices: offenders.ToArray(),
            NotesByPractice: notesByPractice.ToDictionary(k => k.Key, v => v.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> ResolvePractices(RuleResult result, IReadOnlyList<string> selectedPractices)
    {
        if (result.AffectedPractices != null && result.AffectedPractices.Length > 0)
        {
            return result.AffectedPractices
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return _matcher.Match(result.RuleName, selectedPractices);
    }
}
