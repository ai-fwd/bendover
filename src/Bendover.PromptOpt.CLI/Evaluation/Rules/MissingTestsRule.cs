using System;
using System.Linq;

namespace Bendover.PromptOpt.CLI.Evaluation.Rules;

public class MissingTestsRule : IEvaluatorRule
{
    public RuleResult Evaluate(EvaluationContext context)
    {
        var prodChanges = context.ChangedFiles
            .Where(f => IsProductionCode(f.Path))
            .ToList();

        var testChanges = context.ChangedFiles
            .Where(f => IsTestCode(f.Path))
            .ToList();

        if (prodChanges.Any() && !testChanges.Any())
        {
            return new RuleResult(
                "MissingTestsRule",
                true, // Passed "Hard gate", but applies penalty
                -0.2f,
                new[] { "Production code changed without accompanying tests." },
                IsHardFailure: false
            );
        }

        return new RuleResult("MissingTestsRule", true, 0f, Array.Empty<string>());
    }

    private bool IsProductionCode(string path)
    {
        // Must be in src/
        if (!path.StartsWith("src/", StringComparison.OrdinalIgnoreCase)) return false;

        // Exclude docs, markdown
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Contains("/docs/", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private bool IsTestCode(string path)
    {
        if (path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains(".Tests", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase)) return true;
        
        return false;
    }
}
