namespace Mystro.Application.Evaluation;

public interface IEvaluatorRule
{
    RuleResult Evaluate(EvaluationContext context);
}
