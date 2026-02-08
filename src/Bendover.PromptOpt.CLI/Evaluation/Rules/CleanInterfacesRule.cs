using System;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using Bendover.Application.Evaluation;

namespace Bendover.PromptOpt.CLI.Evaluation.Rules;

public class CleanInterfacesRule : IEvaluatorRule
{
    public RuleResult Evaluate(EvaluationContext context)
    {
        // Simple regex to find "new" interfaces (lines starting with + containing interface I...)
        // Regex: ^\+\s*public\s+interface\s+(I\w+)
        
        var diff = context.DiffContent;
        if (string.IsNullOrEmpty(diff)) return this.CreateRuleResult(true, 0f, Array.Empty<string>());

        var matches = Regex.Matches(diff, @"^\+\s*.*interface\s+(I\w+)", RegexOptions.Multiline);
        
        var penalties = new List<string>();

        foreach (Match match in matches)
        {
            var interfaceName = match.Groups[1].Value;
            
            // Count occurances of ": interfaceName" or ", interfaceName" in the diff
            // Only count added lines? Yes, looking for implementations in THIS diff.
            // Regex: \+\s*class\s+\w+\s*:\s*.*\bIThing\b
            // Or simpler: ": IThing"
            // Note: `class Foo : Bar, IThing`
            
            var pattern = $@"\+\s*.*:\s*.*\b{interfaceName}\b"; 
            // This is loose but works for "class Foo : IInterface" or "class Foo : Base, IInterface"
            // Assuming it appears on a line starting with +
            
            var implCount = Regex.Matches(diff, pattern, RegexOptions.Multiline).Count;
            
            // Also matching comma separator: ", IThing"
             var patternComma = $@"\+\s*.*,\s.*\b{interfaceName}\b"; 
             var implCountComma = Regex.Matches(diff, patternComma, RegexOptions.Multiline).Count;

             var totalImpls = implCount + implCountComma;

             if (totalImpls <= 1)
             {
                 penalties.Add($"Interface {interfaceName} has {totalImpls} implementation(s) in diff.");
             }
        }

        if (penalties.Any())
        {
            // Apply penalty once or per interface? Requirement says "apply penalty".
            // Weight 0.1. I'll apply flat 0.1 if ANY found, as weights are usually per rule.
            return this.CreateRuleResult(true, -0.1f, penalties.ToArray());
        }

        return this.CreateRuleResult(true, 0f, Array.Empty<string>());
    }
}
