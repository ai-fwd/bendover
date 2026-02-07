using System.Text;

namespace Bendover.Application.Evaluation;

public class RulePracticeConventionMatcher
{
    public IReadOnlyList<string> Match(string ruleName, IEnumerable<string>? selectedPractices)
    {
        if (string.IsNullOrWhiteSpace(ruleName) || selectedPractices == null)
        {
            return Array.Empty<string>();
        }

        var normalizedRule = Normalize(ruleName);
        var matches = new List<string>();

        foreach (var practice in selectedPractices)
        {
            if (string.IsNullOrWhiteSpace(practice))
            {
                continue;
            }

            var expectedRuleName = $"{Normalize(practice)}rule";
            if (normalizedRule.Equals(expectedRuleName, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(practice);
            }
        }

        return matches;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
