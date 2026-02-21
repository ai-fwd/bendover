using System.Text.RegularExpressions;
using Bendover.Domain.Agentic;
using Bendover.Domain.Entities;
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
                Action: new AgenticStepAction(AgenticStepActionKind.Unknown),
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
        var actionableStepCount = 0;
        var mutationStepCount = 0;
        var verificationStepCount = 0;
        AgenticStepAction? firstAction = null;
        string? firstToolCall = null;

        var invocationInfos = root
            .DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(ClassifyInvocation)
            .ToList();

        foreach (var invocationInfo in invocationInfos)
        {
            if (!string.IsNullOrWhiteSpace(invocationInfo.Violation))
            {
                violations.Add(invocationInfo.Violation!);
            }

            if (invocationInfo.Action is null)
            {
                continue;
            }

            firstAction ??= invocationInfo.Action;
            firstToolCall ??= invocationInfo.ToolCall;
            actionableStepCount++;

            if (invocationInfo.IsMutation)
            {
                mutationStepCount++;
            }

            if (invocationInfo.IsVerification)
            {
                verificationStepCount++;
            }
        }

        if (mutationStepCount > 1)
        {
            violations.Add("contains multiple mutation actions; exactly one mutation action is allowed per step");
        }

        if (mutationStepCount > 0 && verificationStepCount > 0)
        {
            violations.Add("mixes mutation and verification in one step");
        }

        if (verificationStepCount > 1)
        {
            violations.Add("contains multiple verification actions; exactly one verification action is allowed per step");
        }

        if (actionableStepCount > 1)
        {
            violations.Add("contains multiple actionable steps; exactly one action is allowed per step");
        }

        if (actionableStepCount == 0)
        {
            violations.Add("must include exactly one actionable step: sdk.File.Write(...), sdk.File.Delete(...), sdk.Shell.Execute(\"dotnet build ...\"), sdk.Shell.Execute(\"dotnet test ...\"), sdk.Shell.Execute(\"<read-only command>\"), sdk.Done(), or sdk.Signal.Done()");
        }

        var loopNodes = root
            .DescendantNodes()
            .Where(node => node is ForStatementSyntax
                           or ForEachStatementSyntax
                           or WhileStatementSyntax
                           or DoStatementSyntax)
            .ToArray();

        foreach (var loopNode in loopNodes)
        {
            var loopHasMutation = loopNode
                .DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Select(ClassifyInvocation)
                .Any(info => info.IsMutation);

            if (loopHasMutation)
            {
                violations.Add("contains mutation actions inside loops");
                break;
            }
        }

        return new ProtocolAnalysis(
            Action: firstAction ?? new AgenticStepAction(AgenticStepActionKind.Unknown),
            ToolCall: firstToolCall);
    }

    private static InvocationInfo ClassifyInvocation(InvocationExpressionSyntax invocation)
    {
        var expressionText = invocation.Expression
            .ToString()
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (expressionText.Equals("sdk.File.Write", StringComparison.OrdinalIgnoreCase)
            || expressionText.Equals("sdk.WriteFile", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(
                IsMutation: true,
                IsVerification: false,
                Action: new AgenticStepAction(AgenticStepActionKind.MutationWrite, "sdk.File.Write"),
                Violation: null,
                ToolCall: RenderToolCall(invocation));
        }

        if (expressionText.Equals("sdk.File.Delete", StringComparison.OrdinalIgnoreCase)
            || expressionText.Equals("sdk.DeleteFile", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(
                IsMutation: true,
                IsVerification: false,
                Action: new AgenticStepAction(AgenticStepActionKind.MutationDelete, "sdk.File.Delete"),
                Violation: null,
                ToolCall: RenderToolCall(invocation));
        }

        if (expressionText.Equals("sdk.Signal.Done", StringComparison.OrdinalIgnoreCase)
            || expressionText.Equals("sdk.Done", StringComparison.OrdinalIgnoreCase))
        {
            if (invocation.ArgumentList.Arguments.Count != 0)
            {
                return new InvocationInfo(
                    IsMutation: false,
                    IsVerification: false,
                    Action: null,
                    Violation: "sdk.Done()/sdk.Signal.Done() does not accept arguments",
                    ToolCall: null);
            }

            return new InvocationInfo(
                IsMutation: false,
                IsVerification: false,
                Action: new AgenticStepAction(AgenticStepActionKind.Complete, expressionText.Equals("sdk.Done", StringComparison.OrdinalIgnoreCase) ? "sdk.Done" : "sdk.Signal.Done"),
                Violation: null,
                ToolCall: RenderToolCall(invocation));
        }

        if (expressionText.StartsWith("sdk.Git.", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(
                IsMutation: false,
                IsVerification: false,
                Action: null,
                Violation: "sdk.Git operations are not allowed in single-step mode; use sdk.File.Write or sdk.File.Delete",
                ToolCall: null);
        }

        if (!expressionText.Equals("sdk.Shell.Execute", StringComparison.OrdinalIgnoreCase)
            && !expressionText.Equals("sdk.Run", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(IsMutation: false, IsVerification: false, Action: null, Violation: null, ToolCall: null);
        }

        if (!TryGetShellCommand(invocation, out var command))
        {
            return new InvocationInfo(
                IsMutation: false,
                IsVerification: false,
                Action: null,
                Violation: "shell command must use a single string-literal argument",
                ToolCall: null);
        }

        if (!ShellCommandPolicy.TryValidateAllowedForEngineer(command, out var violation))
        {
            return new InvocationInfo(
                IsMutation: false,
                IsVerification: false,
                Action: null,
                Violation: violation,
                ToolCall: null);
        }

        if (ShellCommandPolicy.TryGetVerificationKind(command, out var verificationKind))
        {
            return new InvocationInfo(
                IsMutation: false,
                IsVerification: true,
                Action: new AgenticStepAction(verificationKind, command),
                Violation: null,
                ToolCall: RenderToolCall(invocation));
        }

        return new InvocationInfo(
            IsMutation: false,
            IsVerification: false,
            Action: new AgenticStepAction(AgenticStepActionKind.DiscoveryShell, command),
            Violation: null,
            ToolCall: RenderToolCall(invocation));
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

    private static bool TryGetShellCommand(InvocationExpressionSyntax invocation, out string command)
    {
        command = string.Empty;
        if (invocation.ArgumentList.Arguments.Count != 1)
        {
            return false;
        }

        var firstArg = invocation.ArgumentList.Arguments[0].Expression as LiteralExpressionSyntax;
        if (firstArg is null || !firstArg.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return false;
        }

        command = firstArg.Token.ValueText;
        return !string.IsNullOrWhiteSpace(command);
    }

    private readonly record struct InvocationInfo(
        bool IsMutation,
        bool IsVerification,
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
