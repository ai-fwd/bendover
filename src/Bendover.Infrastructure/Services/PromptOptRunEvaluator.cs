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
    private readonly EvaluatorJsonContractWriter _contractWriter;

    public PromptOptRunEvaluator(
        IFileSystem fileSystem,
        EvaluatorEngine evaluator)
    {
        _fileSystem = fileSystem;
        _evaluator = evaluator;
        _contractWriter = new EvaluatorJsonContractWriter();
    }

    public async Task EvaluateAsync(string outDir, string bundlePath)
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
        var selectedPractices = await ReadSelectedPracticesAsync(outDir);
        var allPractices = await ReadAllPracticesAsync(bundlePath);

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
            ChangedFiles: changedFiles,
            SelectedPractices: selectedPractices,
            AllPractices: allPractices
        );

        var evaluation = _evaluator.Evaluate(context);
        await WriteJsonAsync(outDir, "evaluator.json", _contractWriter.Serialize(evaluation));
    }

    private async Task<string[]> ReadAllPracticesAsync(string bundlePath)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            return Array.Empty<string>();
        }

        var practicesDir = Path.Combine(bundlePath, "practices");
        if (!_fileSystem.Directory.Exists(practicesDir))
        {
            return Array.Empty<string>();
        }

        var practiceNames = new List<string>();
        foreach (var filePath in _fileSystem.Directory.GetFiles(practicesDir, "*.md"))
        {
            var fallback = Path.GetFileNameWithoutExtension(filePath);
            try
            {
                var content = await _fileSystem.File.ReadAllTextAsync(filePath);
                var name = ParseFrontmatterName(content) ?? fallback;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    practiceNames.Add(name.Trim());
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    practiceNames.Add(fallback);
                }
            }
        }

        return practiceNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? ParseFrontmatterName(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var lines = normalized.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---")
            {
                break;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line.Substring(0, separatorIndex).Trim();
            if (!key.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line.Substring(separatorIndex + 1).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private async Task<string[]> ReadSelectedPracticesAsync(string outDir)
    {
        var outputsPath = Path.Combine(outDir, "outputs.json");
        if (!_fileSystem.File.Exists(outputsPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            var json = await _fileSystem.File.ReadAllTextAsync(outputsPath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            if (!TryGetLeadElement(doc.RootElement, out var leadElement))
            {
                return Array.Empty<string>();
            }

            if (leadElement.ValueKind == JsonValueKind.String)
            {
                var leadText = leadElement.GetString();
                if (string.IsNullOrWhiteSpace(leadText))
                {
                    return Array.Empty<string>();
                }

                return ParsePracticeNames(leadText);
            }

            if (leadElement.ValueKind == JsonValueKind.Array)
            {
                return leadElement
                    .EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
        catch
        {
            // Ignore malformed outputs.json
        }

        return Array.Empty<string>();
    }

    private static bool TryGetLeadElement(JsonElement root, out JsonElement leadElement)
    {
        if (root.TryGetProperty("lead", out leadElement))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("lead", StringComparison.OrdinalIgnoreCase))
            {
                leadElement = property.Value;
                return true;
            }
        }

        leadElement = default;
        return false;
    }

    private static string[] ParsePracticeNames(string rawLeadOutput)
    {
        try
        {
            using var leadDoc = JsonDocument.Parse(rawLeadOutput);
            if (leadDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return leadDoc.RootElement
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString()!)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task WriteJsonAsync(string outDir, string filename, string json)
    {
        var path = Path.Combine(outDir, filename);
        await _fileSystem.File.WriteAllTextAsync(path, json);
    }
}
