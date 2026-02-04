using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;

namespace Bendover.Infrastructure.Services;

public class PromptOptRunEvaluator : IPromptOptRunEvaluator
{
    private readonly IFileSystem _fileSystem;
    private readonly EvaluatorEngine _evaluator;
    private readonly IGitRunner _gitRunner;
    private readonly IDotNetRunner _dotNetRunner;

    public PromptOptRunEvaluator(
        IFileSystem fileSystem,
        EvaluatorEngine evaluator,
        IGitRunner gitRunner,
        IDotNetRunner dotNetRunner)
    {
        _fileSystem = fileSystem;
        _evaluator = evaluator;
        _gitRunner = gitRunner;
        _dotNetRunner = dotNetRunner;
    }

    public async Task EvaluateAsync(string outDir)
    {
        _fileSystem.Directory.CreateDirectory(outDir);

        // Capture git diff
        try
        {
            var diff = await _gitRunner.RunAsync("diff");
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(outDir, "git_diff.patch"), diff);
        }
        catch (Exception ex)
        {
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(outDir, "git_diff_error.txt"), ex.Message);
        }

        // Run dotnet test
        string testOutput = "";
        try
        {
            // Running at solution root
            testOutput = await _dotNetRunner.RunAsync("test");
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(outDir, "dotnet_test.txt"), testOutput);
        }
        catch (Exception ex)
        {
            testOutput = $"Error running tests: {ex.Message}";
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(outDir, "dotnet_test_error.txt"), testOutput);
        }

        // Evaluator
        var changedFiles = new List<FileDiff>();
        try
        {
            if (_fileSystem.File.Exists(Path.Combine(outDir, "git_diff.patch")))
            {
                var diffContent = await _fileSystem.File.ReadAllTextAsync(Path.Combine(outDir, "git_diff.patch"));
                changedFiles = DiffParser.Parse(diffContent).ToList();
            }
        }
        catch { /* ignore parsing errors */ }

        var context = new EvaluationContext(
            DiffContent: "",
            TestOutput: testOutput,
            ChangedFiles: changedFiles
        );

        var evaluation = _evaluator.Evaluate(context);
        await WriteJsonAsync(outDir, "evaluator.json", evaluation);
    }

    private async Task WriteJsonAsync(string outDir, string filename, object data)
    {
        var path = Path.Combine(outDir, filename);
        var options = new JsonSerializerOptions { WriteIndented = true };
        await _fileSystem.File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, options));
    }
}
