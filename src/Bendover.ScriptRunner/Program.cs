using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Bendover.ScriptRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = RunnerArguments.TryParse(args);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine(parsed.Error);
            PrintUsage();
            return 2;
        }

        var bodyContent = await ResolveBodyContentAsync(parsed.BodyFilePath, parsed.BodyText);
        var validationError = EngineerBodyValidator.Validate(bodyContent);
        if (validationError is not null)
        {
            Console.Error.WriteLine(validationError);
            return 1;
        }

        try
        {
            var scriptOptions = ScriptOptions.Default
                .AddReferences(typeof(ScriptGlobals).Assembly)
                .AddReferences(typeof(Bendover.SDK.BendoverSDK).Assembly)
                .AddReferences(typeof(Bendover.Domain.Interfaces.IBendoverSDK).Assembly)
                .AddImports(
                    "System",
                    "System.IO",
                    "System.Linq",
                    "System.Collections.Generic"
                );
            await CSharpScript.RunAsync(bodyContent, scriptOptions, new ScriptGlobals());
            return 0;
        }
        catch (Microsoft.CodeAnalysis.Scripting.CompilationErrorException ex)
        {
            foreach (var diagnostic in ex.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic.ToString());
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.ToString());
            return 1;
        }
    }

    private static async Task<string> ResolveBodyContentAsync(string? bodyFilePath, string? bodyText)
    {
        if (!string.IsNullOrWhiteSpace(bodyFilePath))
        {
            return await File.ReadAllTextAsync(bodyFilePath);
        }

        return bodyText ?? string.Empty;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  Bendover.ScriptRunner.dll --body-file <path>");
        Console.Error.WriteLine("  Bendover.ScriptRunner.dll --body <text>");
    }

    private readonly record struct RunnerArguments(string? BodyFilePath, string? BodyText, string? Error)
    {
        public static RunnerArguments TryParse(string[] args)
        {
            string? bodyFilePath = null;
            string? bodyText = null;

            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];

                if (string.Equals(token, "--body-file", StringComparison.Ordinal))
                {
                    if (!TryReadValue(args, index, out var value, out var error))
                    {
                        return new RunnerArguments(null, null, error);
                    }

                    bodyFilePath = value;
                    index++;
                    continue;
                }

                if (string.Equals(token, "--body", StringComparison.Ordinal))
                {
                    if (!TryReadValue(args, index, out var value, out var error))
                    {
                        return new RunnerArguments(null, null, error);
                    }

                    bodyText = value;
                    index++;
                    continue;
                }

                return new RunnerArguments(null, null, $"Unknown argument '{token}'.");
            }

            if (string.IsNullOrWhiteSpace(bodyFilePath) && string.IsNullOrWhiteSpace(bodyText))
            {
                return new RunnerArguments(null, null, "Either --body-file or --body must be provided.");
            }

            return new RunnerArguments(bodyFilePath, bodyText, null);
        }

        private static bool TryReadValue(string[] args, int index, out string? value, out string? error)
        {
            if (index + 1 >= args.Length)
            {
                value = null;
                error = $"Missing value for '{args[index]}'.";
                return false;
            }

            value = args[index + 1];
            error = null;
            return true;
        }
    }
}
