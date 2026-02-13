using System.Reflection;
using System.Text;

namespace Bendover.SDK;

public static class SdkSurfaceDescriber
{
    public const string ContractHeading = "# SDK Tool Usage Contract (Auto-generated)";

    public static string BuildMarkdown()
    {
        var facadeType = typeof(SdkFacade);
        var facadeProperties = facadeType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();
        var facadeMethods = facadeType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToArray();

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
        builder.AppendLine($"- Type: `{FormatTypeName(facadeType)}`");
        builder.AppendLine();

        builder.AppendLine("## Direct sdk Members");
        if (facadeMethods.Length == 0 && facadeProperties.Length == 0)
        {
            builder.AppendLine("- (none)");
        }
        else
        {
            foreach (var property in facadeProperties)
            {
                builder.AppendLine($"- `{property.Name}`: `{FormatTypeName(property.PropertyType)}`");
            }

            foreach (var method in facadeMethods)
            {
                builder.AppendLine($"- `{FormatMethodSignature(method)}`");
            }
        }

        foreach (var property in facadeProperties)
        {
            builder.AppendLine();
            builder.AppendLine($"## sdk.{property.Name} ({FormatTypeName(property.PropertyType)})");
            builder.AppendLine(GetToolCategoryNote(property.Name));

            var methods = property.PropertyType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => !m.IsSpecialName)
                .OrderBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.GetParameters().Length)
                .ToArray();

            if (methods.Length == 0)
            {
                builder.AppendLine("- (no public methods)");
                continue;
            }

            foreach (var method in methods)
            {
                builder.AppendLine($"- `{FormatMethodSignature(method)}`");
            }
        }

        return builder.ToString();
    }

    private static string GetToolCategoryNote(string propertyName)
    {
        return propertyName switch
        {
            "File" => "_Use file tools for read/write/exists checks in the repository workspace._",
            "Git" => "_Use git tools for repository operations._",
            "Shell" => "_Use shell tools for command execution and command output capture._",
            _ => "_Use this SDK surface as documented below._"
        };
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
}
