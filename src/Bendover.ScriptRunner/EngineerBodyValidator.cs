using System.Text.RegularExpressions;
using Bendover.Domain.Entities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bendover.ScriptRunner;

internal static class EngineerBodyValidator
{
    private static readonly Regex MarkdownFenceRegex = new(@"(^|\r?\n)\s*```", RegexOptions.Compiled);

    private static readonly Dictionary<string, ActionSignature> SupportedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sdk.WriteFile"] = new ActionSignature("write_file", "sdk.WriteFile", ExpectsNoArgs: false, IsDone: false),
        ["sdk.DeleteFile"] = new ActionSignature("delete_file", "sdk.DeleteFile", ExpectsNoArgs: false, IsDone: false),
        ["sdk.ReadFile"] = new ActionSignature("read_file", "sdk.ReadFile", ExpectsNoArgs: false, IsDone: false),
        ["sdk.LocateFile"] = new ActionSignature("locate_file", "sdk.LocateFile", ExpectsNoArgs: false, IsDone: false),
        ["sdk.InspectFile"] = new ActionSignature("inspect_file", "sdk.InspectFile", ExpectsNoArgs: false, IsDone: false),
        ["sdk.Build"] = new ActionSignature("build", "sdk.Build", ExpectsNoArgs: true, IsDone: false),
        ["sdk.Test"] = new ActionSignature("test", "sdk.Test", ExpectsNoArgs: true, IsDone: false),
        ["sdk.Done"] = new ActionSignature("done", "sdk.Done", ExpectsNoArgs: true, IsDone: true)
    };

    public static EngineerBodyAnalysis Analyze(string bodyContent)
    {
        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(bodyContent))
        {
            violations.Add("body is empty");
            return new EngineerBodyAnalysis(
                ValidationError: BuildErrorMessage(violations),
                Action: new AgenticStepAction("unknown"),
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
        var protocol = ValidateSingleActionProtocol(root, violations);

        return new EngineerBodyAnalysis(
            ValidationError: violations.Count == 0 ? null : BuildErrorMessage(violations),
            Action: protocol.Action,
            StepPlan: stepPlan,
            ToolCall: protocol.ToolCall);
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

    private static ProtocolAnalysis ValidateSingleActionProtocol(CompilationUnitSyntax root, List<string> violations)
    {
        var actionable = new List<InvocationInfo>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var info = ClassifyInvocation(invocation);
            if (!string.IsNullOrWhiteSpace(info.Violation))
            {
                violations.Add(info.Violation!);
            }

            if (info.Action is not null)
            {
                actionable.Add(info);
            }
        }

        if (actionable.Count == 0)
        {
            violations.Add("must include exactly one actionable step: sdk.WriteFile(...), sdk.DeleteFile(...), sdk.ReadFile(...), sdk.LocateFile(...), sdk.InspectFile(...), sdk.Build(), sdk.Test(), or sdk.Done()");
            return new ProtocolAnalysis(
                Action: new AgenticStepAction("unknown"),
                ToolCall: null);
        }

        var first = actionable[0];

        if (actionable.Count > 1)
        {
            violations.Add("contains multiple actionable steps; exactly one action is allowed per step");
        }

        if (ContainsNestedSdkInvocation(first.Invocation))
        {
            violations.Add("contains nested sdk calls; each step must execute exactly one atomic sdk action");
        }

        return new ProtocolAnalysis(
            Action: first.Action!,
            ToolCall: first.ToolCall);
    }

    private static bool ContainsNestedSdkInvocation(InvocationExpressionSyntax rootInvocation)
    {
        foreach (var invocation in rootInvocation.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (ReferenceEquals(invocation, rootInvocation))
            {
                continue;
            }

            var expressionText = NormalizeInvocationExpression(invocation.Expression);
            if (expressionText.StartsWith("sdk.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static InvocationInfo ClassifyInvocation(InvocationExpressionSyntax invocation)
    {
        var expressionText = NormalizeInvocationExpression(invocation.Expression);

        if (expressionText.StartsWith("sdk.File.", StringComparison.OrdinalIgnoreCase)
            || expressionText.StartsWith("sdk.Shell.", StringComparison.OrdinalIgnoreCase)
            || expressionText.StartsWith("sdk.Git.", StringComparison.OrdinalIgnoreCase)
            || expressionText.Equals("sdk.Run", StringComparison.OrdinalIgnoreCase)
            || expressionText.StartsWith("sdk.Signal.", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(
                Invocation: invocation,
                Action: null,
                Violation: "legacy sdk surface is not supported; use direct sdk methods (WriteFile, DeleteFile, ReadFile, LocateFile, InspectFile, Build, Test, Done)",
                ToolCall: null);
        }

        if (!expressionText.StartsWith("sdk.", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(
                Invocation: invocation,
                Action: null,
                Violation: null,
                ToolCall: null);
        }

        if (!SupportedActions.TryGetValue(expressionText, out var signature))
        {
            return new InvocationInfo(
                Invocation: invocation,
                Action: null,
                Violation: $"unknown sdk action '{expressionText}'; use sdk.WriteFile/DeleteFile/ReadFile/LocateFile/InspectFile/Build/Test/Done",
                ToolCall: null);
        }

        if (signature.ExpectsNoArgs && invocation.ArgumentList.Arguments.Count != 0)
        {
            return new InvocationInfo(
                Invocation: invocation,
                Action: null,
                Violation: $"{signature.CanonicalCall}() does not accept arguments",
                ToolCall: null);
        }

        return new InvocationInfo(
            Invocation: invocation,
            Action: new AgenticStepAction(
                ActionName: signature.ActionName,
                IsDone: signature.IsDone,
                Command: signature.CanonicalCall),
            Violation: null,
            ToolCall: RenderToolCall(invocation));
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

    private readonly record struct ActionSignature(
        string ActionName,
        string CanonicalCall,
        bool ExpectsNoArgs,
        bool IsDone);

    private readonly record struct InvocationInfo(
        InvocationExpressionSyntax Invocation,
        AgenticStepAction? Action,
        string? Violation,
        string? ToolCall);

    private readonly record struct ProtocolAnalysis(
        AgenticStepAction Action,
        string? ToolCall);
}

internal readonly record struct EngineerBodyAnalysis(
    string? ValidationError,
    AgenticStepAction Action,
    string? StepPlan,
    string? ToolCall
);
