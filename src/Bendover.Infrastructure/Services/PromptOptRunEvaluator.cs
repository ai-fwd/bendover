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

    public PromptOptRunEvaluator(
        IFileSystem fileSystem,
        EvaluatorEngine evaluator)
    {
        _fileSystem = fileSystem;
        _evaluator = evaluator;
    }

    public async Task EvaluateAsync(string outDir)
    {
        _fileSystem.Directory.CreateDirectory(outDir);

        var diffPath = Path.Combine(outDir, "git_diff.patch");
        var testPath = Path.Combine(outDir, "dotnet_test.txt");
        var diffContent = _fileSystem.File.Exists(diffPath)
            ? await _fileSystem.File.ReadAllTextAsync(diffPath)
            : string.Empty;
        var testOutput = _fileSystem.File.Exists(testPath)
            ? await _fileSystem.File.ReadAllTextAsync(testPath)
            : string.Empty;

        // Evaluator
        var changedFiles = new List<FileDiff>();
        try
        {
            if (!string.IsNullOrWhiteSpace(diffContent))
            {
                changedFiles = DiffParser.Parse(diffContent).ToList();
            }
        }
        catch { /* ignore parsing errors */ }

        var context = new EvaluationContext(
            DiffContent: diffContent,
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
