using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Bendover.Domain.Entities;
using System.Text.Json;

namespace Bendover.ScriptRunner;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var parsed = RunnerArguments.TryParse(args);
        if (parsed.Error is not null)
        {
            await TryWriteResultFileAsync(
                parsed.ResultFilePath,
                new EngineerBodyAnalysis(
                    ValidationError: null,
                    Action: new AgenticStepAction(AgenticStepActionKind.Unknown),
                    StepPlan: null,
                    ToolCall: null));

            Console.Error.WriteLine(parsed.Error);
            PrintUsage();
            return 2;
        }

        var bodyContent = await ResolveBodyContentAsync(parsed.BodyFilePath, parsed.BodyText);
        var analysis = EngineerBodyValidator.Analyze(bodyContent);
        await TryWriteResultFileAsync(parsed.ResultFilePath, analysis);

        if (analysis.ValidationError is not null)
        {
            Console.Error.WriteLine(analysis.ValidationError);
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
        Console.Error.WriteLine("  Bendover.ScriptRunner.dll --body-file <path> --result-file <path>");
    }

    private static async Task WriteResultFileAsync(string path, EngineerBodyAnalysis analysis)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = new ScriptRunnerActionResult(
            kind: analysis.Action.KindToken,
            command: analysis.Action.Command,
            step_plan: analysis.StepPlan,
            tool_call: analysis.ToolCall);
        var json = JsonSerializer.Serialize(payload);
        await File.WriteAllTextAsync(fullPath, json);
    }

    private static async Task TryWriteResultFileAsync(string? path, EngineerBodyAnalysis analysis)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await WriteResultFileAsync(path, analysis);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write result file '{path}': {ex.Message}");
        }
    }

    private readonly record struct RunnerArguments(
        string? BodyFilePath,
        string? BodyText,
        string? ResultFilePath,
        string? Error)
    {
        public static RunnerArguments TryParse(string[] args)
        {
            string? bodyFilePath = null;
            string? bodyText = null;
            string? resultFilePath = null;

            for (var index = 0; index < args.Length; index++)
            {
                var token = args[index];

                if (string.Equals(token, "--body-file", StringComparison.Ordinal))
                {
                    if (!TryReadValue(args, index, out var value, out var error))
                    {
                        return new RunnerArguments(bodyFilePath, bodyText, resultFilePath, error);
                    }

                    bodyFilePath = value;
                    index++;
                    continue;
                }

                if (string.Equals(token, "--body", StringComparison.Ordinal))
                {
                    if (!TryReadValue(args, index, out var value, out var error))
                    {
                        return new RunnerArguments(bodyFilePath, bodyText, resultFilePath, error);
                    }

                    bodyText = value;
                    index++;
                    continue;
                }

                if (string.Equals(token, "--result-file", StringComparison.Ordinal))
                {
                    if (!TryReadValue(args, index, out var value, out var error))
                    {
                        return new RunnerArguments(bodyFilePath, bodyText, resultFilePath, error);
                    }

                    resultFilePath = value;
                    index++;
                    continue;
                }

                return new RunnerArguments(bodyFilePath, bodyText, resultFilePath, $"Unknown argument '{token}'.");
            }

            if (string.IsNullOrWhiteSpace(bodyFilePath) && string.IsNullOrWhiteSpace(bodyText))
            {
                return new RunnerArguments(bodyFilePath, bodyText, resultFilePath, "Either --body-file or --body must be provided.");
            }

            return new RunnerArguments(bodyFilePath, bodyText, resultFilePath, null);
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

    private readonly record struct ScriptRunnerActionResult(
        string kind,
        string? command,
        string? step_plan,
        string? tool_call);
}
