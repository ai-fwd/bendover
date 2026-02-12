using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Bendover.ScriptRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var describeArgs = DescribeSdkArguments.TryParse(args);
        if (describeArgs.IsDescribeSdk)
        {
            if (describeArgs.Error is not null)
            {
                Console.Error.WriteLine(describeArgs.Error);
                PrintUsage();
                return 2;
            }

            var markdown = SdkSurfaceDescriber.BuildMarkdown();
            if (string.IsNullOrWhiteSpace(describeArgs.OutputPath))
            {
                Console.Out.Write(markdown);
            }
            else
            {
                await File.WriteAllTextAsync(describeArgs.OutputPath, markdown);
            }

            return 0;
        }

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
                .AddImports("System");
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
        Console.Error.WriteLine("  Bendover.ScriptRunner.dll --describe-sdk [--out <path>]");
    }

    private readonly record struct DescribeSdkArguments(bool IsDescribeSdk, string? OutputPath, string? Error)
    {
        public static DescribeSdkArguments TryParse(string[] args)
        {
            var isDescribeSdk = false;
            string? outputPath = null;

            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                if (string.Equals(token, "--describe-sdk", StringComparison.Ordinal))
                {
                    isDescribeSdk = true;
                    continue;
                }

                if (string.Equals(token, "--out", StringComparison.Ordinal))
                {
                    if (!TryReadValue(args, index, out var value, out var error))
                    {
                        return new DescribeSdkArguments(true, null, error);
                    }

                    outputPath = value;
                    index++;
                }
            }

            if (!isDescribeSdk)
            {
                return new DescribeSdkArguments(false, null, null);
            }

            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];
                if (string.Equals(token, "--describe-sdk", StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(token, "--out", StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                return new DescribeSdkArguments(true, null, $"Unknown argument '{token}' for describe-sdk mode.");
            }

            return new DescribeSdkArguments(true, outputPath, null);
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
