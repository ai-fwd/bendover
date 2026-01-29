namespace Bendover.PromptOpt.CLI.Evaluation;

public interface IEvaluatorRule
{
    RuleResult Evaluate(EvaluationContext context);
}
