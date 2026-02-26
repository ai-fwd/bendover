using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Bendover.Application.Evaluation;

namespace Bendover.PromptOpt.CLI.Evaluation.Rules;

public class CodingStyleRule : IEvaluatorRule
{
    private static readonly Regex StringEqualsChainPattern = new(
        @"string\.Equals\(\s*(?<lhs>[^,\)]+?)\s*,[^)]*\)\s*\|\|\s*string\.Equals\(\s*\k<lhs>\s*,",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex EqualityChainPattern = new(
        @"(?<lhs>[A-Za-z_][A-Za-z0-9_\.]*)\s*==\s*[^|;]+?\s*\|\|\s*\k<lhs>\s*==",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public RuleResult Evaluate(EvaluationContext context)
    {
        var addedCSharpSnippet = ExtractAddedCSharpCode(context.DiffContent);
        if (string.IsNullOrWhiteSpace(addedCSharpSnippet))
        {
            return this.CreateRuleResult(true, 0f, Array.Empty<string>());
        }

        var normalized = NormalizeWhitespace(addedCSharpSnippet);
        var notes = new List<string>();

        if (StringEqualsChainPattern.IsMatch(normalized))
        {
            notes.Add("Avoid chained '||' equality checks for membership. Use HashSet<T> with Contains(...).");
        }

        if (EqualityChainPattern.IsMatch(normalized))
        {
            notes.Add("Avoid chained '==' membership checks with '||'. Use HashSet<T> with Contains(...).");
        }

        if (notes.Count == 0)
        {
            return this.CreateRuleResult(true, 0f, Array.Empty<string>());
        }

        return this.CreateRuleResult(
            passed: false,
            scoreDelta: -0.1f,
            notes: notes.ToArray(),
            isHardFailure: false);
    }

    private static string ExtractAddedCSharpCode(string diffContent)
    {
        if (string.IsNullOrWhiteSpace(diffContent))
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var lines = diffContent.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        string? currentPath = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                currentPath = null;
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                currentPath = ParseTargetPath(line);
                continue;
            }

            if (!IsCSharpPath(currentPath))
            {
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                builder.AppendLine(line.Substring(1));
            }
        }

        return builder.ToString();
    }

    private static string? ParseTargetPath(string line)
    {
        var path = line.Substring(4).Trim();
        if (string.Equals(path, "/dev/null", StringComparison.Ordinal))
        {
            return null;
        }

        return path.StartsWith("b/", StringComparison.Ordinal) ? path.Substring(2) : path;
    }

    private static bool IsCSharpPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
               && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespace(string content)
    {
        return Regex.Replace(content, @"\s+", " ").Trim();
    }
}
