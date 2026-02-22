using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Bendover.SDK;

public static class SdkSurfaceDescriber
{
    public const string ContractHeading = "# SDK Tool Usage Contract (Auto-generated)";

    public static string BuildMarkdown()
    {
        var sdkType = typeof(BendoverSDK);
        var methods = sdkType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

        var docsByMethod = LoadToolDocsByMethodName(sdkType.Assembly, sdkType.FullName!);

        var builder = new StringBuilder();
        builder.AppendLine(ContractHeading);
        builder.AppendLine();
        builder.AppendLine("- The Engineer is responsible for solving the task.");
        builder.AppendLine("- If tool usage is needed, use the preloaded `sdk` global.");
        builder.AppendLine("- Output must be BODY-only C# script statements.");
        builder.AppendLine("- The script should call SDK tools to achieve the goal.");
        builder.AppendLine();

        builder.AppendLine("## Entry Point");
        builder.AppendLine("- Global variable: `sdk`");
        builder.AppendLine($"- Type: `{FormatTypeName(sdkType)}`");
        builder.AppendLine();

        builder.AppendLine("## sdk Methods");
        foreach (var method in methods)
        {
            if (!docsByMethod.TryGetValue(method.Name, out var doc))
            {
                throw new InvalidOperationException(
                    $"Missing XML tool documentation for method '{sdkType.FullName}.{method.Name}'.");
            }

            EnsureRequiredDocField(method, "summary", doc.Summary);
            EnsureRequiredDocField(method, "tool_category", doc.ToolCategory);
            EnsureRequiredDocField(method, "use_instead_of", doc.UseInsteadOf);
            EnsureRequiredDocField(method, "result_visibility", doc.ResultVisibility);

            builder.AppendLine($"- `{FormatMethodSignature(method)}`");
            builder.AppendLine($"  - Category: `{doc.ToolCategory}`");
            builder.AppendLine($"  - Summary: {doc.Summary}");
            builder.AppendLine($"  - Use this instead of: `{doc.UseInsteadOf}`");
            builder.AppendLine($"  - Result visibility: {doc.ResultVisibility}");
            foreach (var paramRule in doc.ParamRules)
            {
                builder.AppendLine($"  - Param `{paramRule.Name}`: {paramRule.Rule}");
            }
        }

        return builder.ToString();
    }

    private static void EnsureRequiredDocField(MethodInfo method, string fieldName, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Method '{method.DeclaringType?.FullName}.{method.Name}' is missing required XML doc tag '{fieldName}'.");
    }

    private static Dictionary<string, ToolDoc> LoadToolDocsByMethodName(Assembly assembly, string fullTypeName)
    {
        var xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath))
        {
            throw new InvalidOperationException(
                $"Could not load SDK XML documentation file '{xmlPath}'. Ensure GenerateDocumentationFile is enabled.");
        }

        var document = XDocument.Load(xmlPath);
        var members = document.Descendants("member");
        var prefix = $"M:{fullTypeName}.";

        var docs = new Dictionary<string, ToolDoc>(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var nameAttribute = member.Attribute("name")?.Value;
            if (string.IsNullOrWhiteSpace(nameAttribute)
                || !nameAttribute.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var methodSegment = nameAttribute.Substring(prefix.Length);
            var openParenIndex = methodSegment.IndexOf('(');
            var methodName = openParenIndex >= 0
                ? methodSegment[..openParenIndex]
                : methodSegment;
            if (string.IsNullOrWhiteSpace(methodName))
            {
                continue;
            }

            var summary = Normalize(member.Element("summary")?.Value);
            var toolCategory = Normalize(member.Element("tool_category")?.Value);
            var useInsteadOf = Normalize(member.Element("use_instead_of")?.Value);
            var resultVisibility = Normalize(member.Element("result_visibility")?.Value);
            var paramRules = member.Elements("param_rule")
                .Select(x => new ParamRule(
                    Name: Normalize(x.Attribute("name")?.Value) ?? "(unspecified)",
                    Rule: Normalize(x.Value) ?? string.Empty))
                .Where(x => !string.IsNullOrWhiteSpace(x.Rule))
                .ToArray();

            docs[methodName] = new ToolDoc(
                Summary: summary,
                ToolCategory: toolCategory,
                UseInsteadOf: useInsteadOf,
                ResultVisibility: resultVisibility,
                ParamRules: paramRules);
        }

        return docs;
    }

    private static string FormatMethodSignature(MethodInfo method)
    {
        var parameters = string.Join(
            ", ",
            method.GetParameters().Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}"));
        return $"{FormatTypeName(method.ReturnType)} {method.Name}({parameters})";
    }

    private static string FormatTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        var tickIndex = type.Name.IndexOf('`', StringComparison.Ordinal);
        var typeName = tickIndex > 0 ? type.Name[..tickIndex] : type.Name;
        var genericArgs = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
        return $"{typeName}<{genericArgs}>";
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            " ",
            value
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    private sealed record ToolDoc(
        string? Summary,
        string? ToolCategory,
        string? UseInsteadOf,
        string? ResultVisibility,
        IReadOnlyList<ParamRule> ParamRules);

    private sealed record ParamRule(string Name, string Rule);
}
