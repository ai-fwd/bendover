using System;
using Bendover.Application.Evaluation;

namespace Bendover.PromptOpt.CLI.Evaluation.Rules;

public class ExpectedCodeChangeReplayRule : IEvaluatorRule
{
    public RuleResult Evaluate(EvaluationContext context)
    {
        if (context.PreviousRunHadCodeChanges == true
            && string.IsNullOrWhiteSpace(context.DiffContent))
        {
            return this.CreateRuleResult(
                passed: false,
                scoreDelta: 0f,
                notes: new[]
                {
                    "Previous run required code changes, but replay produced no git diff."
                },
                isHardFailure: true);
        }

        return this.CreateRuleResult(true, 0f, Array.Empty<string>());
    }
}
