using System.Text.RegularExpressions;
using Bendover.Application.Interfaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bendover.Application;

public sealed class EngineerBodyValidator : IEngineerBodyValidator
{
    private static readonly Regex MarkdownFenceRegex = new(@"(^|\r?\n)\s*```", RegexOptions.Compiled);

    public EngineerBodyValidationResult Validate(string bodyContent)
    {
        var violations = new List<string>();

        if (string.IsNullOrWhiteSpace(bodyContent))
        {
            violations.Add("body is empty");
            return EngineerBodyValidationResult.Failure(BuildErrorMessage(violations));
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

        if (root.Members.Any(member => member is not GlobalStatementSyntax))
        {
            violations.Add("contains namespace/type/member declarations");
        }

        if (!root.Members.OfType<GlobalStatementSyntax>().Any())
        {
            violations.Add("must include at least one global statement");
        }

        if (violations.Count > 0)
        {
            return EngineerBodyValidationResult.Failure(BuildErrorMessage(violations));
        }

        return EngineerBodyValidationResult.Success();
    }

    private static string BuildErrorMessage(IReadOnlyCollection<string> violations)
    {
        var detail = string.Join("; ", violations);
        return $"Engineer body rejected: {detail}. Return body-only C# statements (no markdown fences, #r, using, namespace/type/member declarations).";
    }
}
