using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using Bendover.Application.Evaluation;

namespace Bendover.PromptOpt.CLI.Evaluation.Rules;

public class TestFailureRule : IEvaluatorRule
{
    public RuleResult Evaluate(EvaluationContext context)
    {
        var output = context.TestOutput;

        if (string.IsNullOrWhiteSpace(output))
        {
             return Fail("No test output found.");
        }

        if (output.Contains("Build FAILED"))
        {
            return Fail("Build Failed.");
        }

        if (output.Contains("error CS"))
        {
            return Fail("Compilation Errors detected.");
        }

        // Check for specific failure counts
        // Regex for "Failed: N" where N > 0
        if (Regex.IsMatch(output, @"Failed:\s*[1-9]\d*", RegexOptions.IgnoreCase))
        {
            return Fail("Tests failed.");
        }

        // Check for Success confirmation
        // "Total: X, Failed: 0, Succeeded: X"
        // Or "Passed!" or similar. But user asked for specific summary line check if possible.
        // Let's look for "Failed: 0"
        if (Regex.IsMatch(output, @"Failed:\s*0", RegexOptions.IgnoreCase))
        {
             return new RuleResult("TestFailureRule", true, 0f, new string[0]);
        }

        return Fail("Unable to determine test result");
    }

    private RuleResult Fail(string reason)
    {
        return new RuleResult("TestFailureRule", false, 0f, new[] { reason }, IsHardFailure: true);
    }
}
