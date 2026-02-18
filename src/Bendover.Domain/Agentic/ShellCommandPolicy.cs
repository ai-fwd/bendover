using System.Text;
using System.Text.RegularExpressions;
using Bendover.Domain.Entities;

namespace Bendover.Domain.Agentic;

public static class ShellCommandPolicy
{
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
        "sort ",
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

    public static bool IsVerificationCommand(string command)
    {
        return TryGetVerificationKind(command, out _);
    }

    public static bool TryGetVerificationKind(string command, out AgenticStepActionKind kind)
    {
        kind = AgenticStepActionKind.Unknown;
        var match = VerificationCommandRegex.Match(command ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        var verb = match.Groups[1].Value;
        if (string.Equals(verb, "build", StringComparison.OrdinalIgnoreCase))
        {
            kind = AgenticStepActionKind.VerificationBuild;
            return true;
        }

        if (string.Equals(verb, "test", StringComparison.OrdinalIgnoreCase))
        {
            kind = AgenticStepActionKind.VerificationTest;
            return true;
        }

        return false;
    }

    public static bool IsMutatingCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        if (command.Contains('>'))
        {
            return true;
        }

        var lower = $" {command.Trim().ToLowerInvariant()} ";
        return MutatingShellFragments.Any(fragment => lower.Contains(fragment, StringComparison.Ordinal));
    }

    public static bool IsReadOnlyCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var segments = SplitShellSegments(command);

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
                .Any(prefix => MatchesReadOnlyPrefix(segment, prefix));
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryValidateAllowedForEngineer(string command, out string violationReason)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            violationReason = "command is empty";
            return false;
        }

        if (IsMutatingCommand(command))
        {
            violationReason = $"shell command '{command}' is mutating and not allowed";
            return false;
        }

        if (IsVerificationCommand(command) || IsReadOnlyCommand(command))
        {
            violationReason = string.Empty;
            return true;
        }

        violationReason = $"shell command '{command}' is not in the read/verification allowlist";
        return false;
    }

    private static bool MatchesReadOnlyPrefix(string segment, string prefix)
    {
        if (segment.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Prefixes ending with a space represent command-token prefixes like "rg ".
        // Accept exact token forms too (for example "rg" without arguments).
        if (prefix.EndsWith(' ')
            && segment.Equals(prefix.TrimEnd(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string[] SplitShellSegments(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return Array.Empty<string>();
        }

        var segments = new List<string>();
        var builder = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var escaping = false;

        void Flush()
        {
            var segment = builder.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(segment))
            {
                segments.Add(segment);
            }

            builder.Clear();
        }

        for (var index = 0; index < command.Length; index++)
        {
            var ch = command[index];

            if (escaping)
            {
                builder.Append(ch);
                escaping = false;
                continue;
            }

            if (ch == '\\' && !inSingleQuote)
            {
                builder.Append(ch);
                escaping = true;
                continue;
            }

            if (ch == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                builder.Append(ch);
                continue;
            }

            if (ch == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                builder.Append(ch);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote)
            {
                if (ch == ';')
                {
                    Flush();
                    continue;
                }

                if (ch == '|' && index + 1 < command.Length && command[index + 1] == '|')
                {
                    Flush();
                    index++;
                    continue;
                }

                if (ch == '&' && index + 1 < command.Length && command[index + 1] == '&')
                {
                    Flush();
                    index++;
                    continue;
                }

                if (ch == '|')
                {
                    Flush();
                    continue;
                }
            }

            builder.Append(ch);
        }

        Flush();
        return segments.ToArray();
    }
}
