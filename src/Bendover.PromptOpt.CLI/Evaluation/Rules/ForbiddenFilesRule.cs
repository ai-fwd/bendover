using System;
using System.Linq;
using System.Linq;
using Bendover.Application.Evaluation;

namespace Bendover.PromptOpt.CLI.Evaluation.Rules;

public class ForbiddenFilesRule : IEvaluatorRule
{
    public RuleResult Evaluate(EvaluationContext context)
    {
        var forbidden = context.ChangedFiles
            .Where(f => IsForbidden(f.Path))
            .ToList();

        if (forbidden.Any())
        {
             var notes = forbidden.Select(f => $"Forbidden file modified: {f.Path}").ToArray();
             return this.CreateRuleResult(false, 0f, notes, isHardFailure: true);
        }

        return this.CreateRuleResult(true, 0f, Array.Empty<string>());
    }

    private bool IsForbidden(string path)
    {
        // Check for bin/, obj/ directory segments
        // Check for .lock extension
        
        if (path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return true;

        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        // If any segment is exactly "bin" or "obj" (case insensitive? usually these are lowercase but filesystem might vary)
        if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase) || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
