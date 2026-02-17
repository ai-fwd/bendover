using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bendover.ScriptRunner;

internal static class EngineerBodyValidator
{
    private static readonly Regex MarkdownFenceRegex = new(@"(^|\r?\n)\s*```", RegexOptions.Compiled);
    private static readonly Regex SegmentSplitRegex = new(@"\|\||&&|\||;", RegexOptions.Compiled);
    private static readonly Regex VerificationCommandRegex = new(
        @"^\s*(?:cd\s+\S+\s*&&\s*)?dotnet\s+(build|test)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] ReadOnlyShellPrefixes =
    {
        "cat ",
        "sed ",
        "ls",
        "find ",
        "rg ",
        "grep ",
        "head ",
        "tail ",
        "wc ",
        "pwd",
        "git status",
        "git diff",
        "git log",
        "git show",
        "git rev-parse",
        "which ",
        "dotnet --info",
        "dotnet --list-sdks",
        "dotnet --list-runtimes"
    };

    private static readonly string[] MutatingShellFragments =
    {
        " rm ",
        " rm\t",
        "rm -",
        "mv ",
        "cp ",
        "mkdir ",
        "touch ",
        "truncate ",
        "chmod ",
        "chown ",
        "ln ",
        "git reset",
        "git clean",
        "git checkout",
        "git apply",
        "git am",
        "git commit",
        "git push",
        "git merge",
        "git rebase",
        "git tag"
    };

    public static string? Validate(string bodyContent)
    {
        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(bodyContent))
        {
            violations.Add("body is empty");
            return BuildErrorMessage(violations);
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

        ValidateSingleActionProtocol(root, violations);

        return violations.Count == 0 ? null : BuildErrorMessage(violations);
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

    private static void ValidateSingleActionProtocol(CompilationUnitSyntax root, List<string> violations)
    {
        var actionableStepCount = 0;
        var mutationStepCount = 0;
        var verificationStepCount = 0;
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

            if (invocationInfo.IsMutation)
            {
                mutationStepCount++;
                actionableStepCount++;
            }

            if (invocationInfo.IsVerification)
            {
                verificationStepCount++;
                actionableStepCount++;
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

        if (actionableStepCount == 0)
        {
            violations.Add("must include exactly one actionable step (one mutation or one verification action)");
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
    }

    private static InvocationInfo ClassifyInvocation(InvocationExpressionSyntax invocation)
    {
        var expressionText = invocation.Expression
            .ToString()
            .Replace(" ", string.Empty, StringComparison.Ordinal);

        if (expressionText.Equals("sdk.File.Write", StringComparison.OrdinalIgnoreCase)
            || expressionText.Equals("sdk.WriteFile", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(IsMutation: true, IsVerification: false, Violation: null);
        }

        if (expressionText.Equals("sdk.File.Delete", StringComparison.OrdinalIgnoreCase)
            || expressionText.Equals("sdk.DeleteFile", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(IsMutation: true, IsVerification: false, Violation: null);
        }

        if (expressionText.StartsWith("sdk.Git.", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(
                IsMutation: false,
                IsVerification: false,
                Violation: "sdk.Git operations are not allowed in single-step mode; use sdk.File.Write or sdk.File.Delete");
        }

        if (!expressionText.Equals("sdk.Shell.Execute", StringComparison.OrdinalIgnoreCase)
            && !expressionText.Equals("sdk.Run", StringComparison.OrdinalIgnoreCase))
        {
            return new InvocationInfo(IsMutation: false, IsVerification: false, Violation: null);
        }

        if (!TryGetShellCommand(invocation, out var command))
        {
            return new InvocationInfo(
                IsMutation: false,
                IsVerification: false,
                Violation: "shell command must use a single string-literal argument");
        }

        if (IsMutatingShellCommand(command))
        {
            return new InvocationInfo(
                IsMutation: false,
                IsVerification: false,
                Violation: $"shell command '{command}' is mutating and not allowed");
        }

        if (IsVerificationCommand(command))
        {
            return new InvocationInfo(IsMutation: false, IsVerification: true, Violation: null);
        }

        if (IsReadOnlyShellCommand(command))
        {
            return new InvocationInfo(IsMutation: false, IsVerification: false, Violation: null);
        }

        return new InvocationInfo(
            IsMutation: false,
            IsVerification: false,
            Violation: $"shell command '{command}' is not in the read/verification allowlist");
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

    private static bool IsVerificationCommand(string command)
    {
        return VerificationCommandRegex.IsMatch(command);
    }

    private static bool IsReadOnlyShellCommand(string command)
    {
        var segments = SegmentSplitRegex
            .Split(command)
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        if (segments.Length == 0)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var allowed = ReadOnlyShellPrefixes
                .Any(prefix => segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsMutatingShellCommand(string command)
    {
        if (command.Contains('>'))
        {
            return true;
        }

        var lower = $" {command.Trim().ToLowerInvariant()} ";
        return MutatingShellFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal));
    }

    private readonly record struct InvocationInfo(
        bool IsMutation,
        bool IsVerification,
        string? Violation);
}
