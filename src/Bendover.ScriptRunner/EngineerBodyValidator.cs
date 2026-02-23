using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bendover.ScriptRunner;

internal static class EngineerBodyValidator
{
    private static readonly Regex MarkdownFenceRegex = new(@"(^|\r?\n)\s*```", RegexOptions.Compiled);

    public static EngineerBodyAnalysis Analyze(string bodyContent)
    {
        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(bodyContent))
        {
            violations.Add("body is empty");
            return new EngineerBodyAnalysis(
                ValidationError: BuildErrorMessage(violations),
                StepPlan: null,
                ToolCall: null);
        }

        if (MarkdownFenceRegex.IsMatch(bodyContent))
        {
            violations.Add("contains markdown fences");
        }

        var parseOptions = CSharpParseOptions.Default.WithKind(SourceCodeKind.Script);
        var syntaxTree = CSharpSyntaxTree.ParseText(bodyContent, parseOptions);
        var root = syntaxTree.GetCompilationUnitRoot();

        var hasReferenceDirective = root
            .DescendantTrivia(descendIntoTrivia: true)
            .Select(trivia => trivia.GetStructure())
            .OfType<ReferenceDirectiveTriviaSyntax>()
            .Any();
        if (hasReferenceDirective)
        {
            violations.Add("contains #r directives");
        }

        if (root.Usings.Count > 0)
        {
            violations.Add("contains using directives");
        }

        if (root.Members.Any(member => !IsAllowedScriptMember(member)))
        {
            violations.Add("contains namespace/type/member declarations");
        }

        if (!root.Members.OfType<GlobalStatementSyntax>().Any())
        {
            violations.Add("must include at least one global statement");
        }

        var stepPlan = TryExtractStepPlan(root);
        var toolCall = TryExtractSingleSdkToolCall(root);

        return new EngineerBodyAnalysis(
            ValidationError: violations.Count == 0 ? null : BuildErrorMessage(violations),
            StepPlan: stepPlan,
            ToolCall: toolCall);
    }

    public static string? Validate(string bodyContent)
    {
        return Analyze(bodyContent).ValidationError;
    }

    private static string BuildErrorMessage(IReadOnlyCollection<string> violations)
    {
        var detail = string.Join("; ", violations);
        return $"Engineer body rejected: {detail}. Return body-only C# statements (no markdown fences, #r, using, namespace/type/member declarations).";
    }

    private static bool IsAllowedScriptMember(MemberDeclarationSyntax member)
    {
        return member is GlobalStatementSyntax or FieldDeclarationSyntax;
    }

    private static string? TryExtractSingleSdkToolCall(CompilationUnitSyntax root)
    {
        InvocationExpressionSyntax? toolInvocation = null;

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!IsSdkInvocation(invocation))
            {
                continue;
            }

            if (toolInvocation is not null)
            {
                return null;
            }

            toolInvocation = invocation;
        }

        return toolInvocation is null
            ? null
            : RenderToolCall(toolInvocation);
    }

    private static bool IsSdkInvocation(InvocationExpressionSyntax invocation)
    {
        var expressionText = NormalizeInvocationExpression(invocation.Expression);
        return expressionText.StartsWith("sdk.", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInvocationExpression(ExpressionSyntax expression)
    {
        return expression
            .ToString()
            .Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static string? TryExtractStepPlan(CompilationUnitSyntax root)
    {
        foreach (var member in root.Members)
        {
            if (member is FieldDeclarationSyntax fieldDeclaration
                && TryExtractStepPlanFromDeclaration(fieldDeclaration.Declaration, out var fieldPlan))
            {
                return fieldPlan;
            }

            if (member is not GlobalStatementSyntax globalStatement
                || globalStatement.Statement is not LocalDeclarationStatementSyntax localDeclaration
                || localDeclaration.Declaration is null)
            {
                continue;
            }

            if (TryExtractStepPlanFromDeclaration(localDeclaration.Declaration, out var localPlan))
            {
                return localPlan;
            }
        }

        return null;
    }

    private static bool TryExtractStepPlanFromDeclaration(
        VariableDeclarationSyntax declaration,
        out string? stepPlan)
    {
        stepPlan = null;
        foreach (var variable in declaration.Variables)
        {
            if (!string.Equals(variable.Identifier.ValueText, "__stepPlan", StringComparison.Ordinal))
            {
                continue;
            }

            if (variable.Initializer?.Value is not LiteralExpressionSyntax literal
                || !literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                continue;
            }

            var value = literal.Token.ValueText.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            stepPlan = value;
            return true;
        }

        return false;
    }

    private static string RenderToolCall(InvocationExpressionSyntax invocation)
    {
        return invocation.NormalizeWhitespace().ToString();
    }
}

internal readonly record struct EngineerBodyAnalysis(
    string? ValidationError,
    string? StepPlan,
    string? ToolCall
);
