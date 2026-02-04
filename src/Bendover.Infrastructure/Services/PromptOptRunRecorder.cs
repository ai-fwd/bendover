using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Infrastructure;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace Bendover.Infrastructure.Services;

public class PromptOptRunRecorder : IPromptOptRunRecorder
{
    private readonly IFileSystem _fileSystem;
    private readonly EvaluatorEngine _evaluator;
    private readonly IGitRunner _gitRunner;
    private readonly IDotNetRunner _dotNetRunner;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;
    private string? _runId;
    private string? _runDir;
    private bool _captureEnabled;
    private bool _evaluateEnabled;

    private readonly Dictionary<string, List<ChatMessage>> _prompts = new();
    private readonly Dictionary<string, string> _outputs = new();

    public PromptOptRunRecorder(
        IFileSystem fileSystem,
        EvaluatorEngine evaluator,
        IGitRunner gitRunner,
        IDotNetRunner dotNetRunner,
        IPromptOptRunContextAccessor runContextAccessor)
    {
        _fileSystem = fileSystem;
        _evaluator = evaluator;
        _gitRunner = gitRunner;
        _dotNetRunner = dotNetRunner;
        _runContextAccessor = runContextAccessor;
    }

    public async Task<string> StartRunAsync(string goal, string baseCommit, string bundleId)
    {
        var context = _runContextAccessor.Current;
        if (context == null)
        {
            throw new InvalidOperationException("PromptOpt run context is not set.");
        }

        _runId = string.IsNullOrWhiteSpace(context.RunId)
            ?  $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N").Substring(0, 8)}"
            : context.RunId;
        _runDir = context.OutDir;
        _captureEnabled = context.Capture;
        _evaluateEnabled = context.Evaluate;

        _fileSystem.Directory.CreateDirectory(_runDir);

        if (_captureEnabled)
        {
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "goal.txt"), goal);
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "base_commit.txt"), baseCommit);
            await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "bundle_id.txt"), bundleId);

            var meta = new
            {
                run_id = _runId,
                started_at = DateTime.UtcNow,
                base_commit = baseCommit,
                bundle_id = bundleId,
                goal_hash = goal.GetHashCode() // Simple hash for now
            };
            await WriteJsonAsync("run_meta.json", meta);
        }

        return _runId;
    }



    public Task RecordPromptAsync(string phase, List<ChatMessage> messages)
    {
        // Store byte-for-byte exact messages
        // But we need to convert them to a serializable format or just store the list
        _prompts[phase] = new List<ChatMessage>(messages);
        return Task.CompletedTask;
    }

    public Task RecordOutputAsync(string phase, string output)
    {
        _outputs[phase] = output;
        return Task.CompletedTask;
    }

    public async Task FinalizeRunAsync()
    {
        if (_runDir == null) return;

        if (_captureEnabled)
        {
            var serializedPrompts = new
            {
                phases = _prompts.ToDictionary(k => k.Key, v => v.Value.Select(m => new { role = m.Role.Value, content = m.Text }))
            };

            await WriteJsonAsync("prompts.json", serializedPrompts);

            // Write outputs.json
            await WriteJsonAsync("outputs.json", _outputs);
        }

        if (_evaluateEnabled)
        {
            // Capture git diff
            try
            {
                var diff = await _gitRunner.RunAsync("diff");
                await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "git_diff.patch"), diff);
            }
            catch (Exception ex)
            {
                await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "git_diff_error.txt"), ex.Message);
            }

            // Run dotnet test
            string testOutput = "";
            try
            {
                // Running at solution root
                testOutput = await _dotNetRunner.RunAsync("test");
                await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "dotnet_test.txt"), testOutput);
            }
            catch (Exception ex)
            {
                testOutput = $"Error running tests: {ex.Message}";
                await _fileSystem.File.WriteAllTextAsync(Path.Combine(_runDir, "dotnet_test_error.txt"), testOutput);
            }

            // Evaluator
            // Need to parse diff to get changed files
            var changedFiles = new List<FileDiff>();
            try
            {
                // If diff was captured successfully
                if (_fileSystem.File.Exists(Path.Combine(_runDir, "git_diff.patch")))
                {
                    var diffContent = await _fileSystem.File.ReadAllTextAsync(Path.Combine(_runDir, "git_diff.patch"));
                    changedFiles = DiffParser.Parse(diffContent).ToList();
                }
            }
            catch { /* ignore parsing errors */ }

            var context = new EvaluationContext(
                DiffContent: "", // populated later if I can, or redundant if ChangedFiles has content
                TestOutput: testOutput,
                ChangedFiles: changedFiles
            );

            var evaluation = _evaluator.Evaluate(context);
            await WriteJsonAsync("evaluator.json", evaluation);
        }
    }

    private async Task WriteJsonAsync(string filename, object data)
    {
        if (_runDir == null) return;
        var path = Path.Combine(_runDir, filename);
        var options = new JsonSerializerOptions { WriteIndented = true };
        await _fileSystem.File.WriteAllTextAsync(path, JsonSerializer.Serialize(data, options));
    }
}
