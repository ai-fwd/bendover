using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions.TestingHelpers;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.PromptOpt.CLI.Evaluation.Rules;
using Bendover.Infrastructure.Services;
using Xunit;

namespace Bendover.Tests;

public class PromptOptRunEvaluatorTests
{
    private const string BundlePath = "/bundle";

    [Fact]
    public async Task EvaluateAsync_LoadsArtifactsAndWritesEvaluation()
    {
        // Arrange
        var fileSystem = new MockFileSystem();
        var capturingRule = new CapturingRule();
        var evaluatorEngine = new EvaluatorEngine(new[] { capturingRule });

        var outDir = "/runs/out-1";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(Path.Combine(outDir, "git_diff.patch"), "diff content");
        fileSystem.File.WriteAllText(Path.Combine(outDir, "dotnet_test.txt"), "test passed");
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);

        // Act
        await sut.EvaluateAsync(outDir, BundlePath);

        // Assert
        Assert.True(fileSystem.File.Exists(Path.Combine(outDir, "evaluator.json")));
        Assert.Equal("diff content", capturingRule.LastContext?.DiffContent);
        Assert.Equal("test passed", capturingRule.LastContext?.TestOutput);
    }

    [Fact]
    public async Task Writes_SnakeCase_Contract_With_PracticeAttribution_Object()
    {
        var fileSystem = new MockFileSystem();
        var evaluatorEngine = new EvaluatorEngine(new[] { new FixedRule("TDDSpiritRule", passed: false, notes: new[] { "Write tests first." }) });

        var outDir = "/runs/out-contract";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(Path.Combine(outDir, "outputs.json"), "{ \"lead\": \"[\\\"tdd_spirit\\\"]\" }");
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        var evaluatorPath = Path.Combine(outDir, "evaluator.json");
        var root = JsonDocument.Parse(fileSystem.File.ReadAllText(evaluatorPath)).RootElement;

        Assert.True(root.TryGetProperty("pass", out _));
        Assert.True(root.TryGetProperty("score", out _));
        Assert.True(root.TryGetProperty("flags", out _));
        Assert.True(root.TryGetProperty("notes", out _));
        Assert.True(root.TryGetProperty("practice_attribution", out var practice));
        Assert.True(practice.TryGetProperty("selected_practices", out _));
        Assert.True(practice.TryGetProperty("offending_practices", out _));
        Assert.True(practice.TryGetProperty("notes_by_practice", out _));
    }

    [Fact]
    public async Task Reads_SelectedPractices_From_Outputs_Lead()
    {
        var fileSystem = new MockFileSystem();
        var capturingRule = new CapturingRule();
        var evaluatorEngine = new EvaluatorEngine(new[] { capturingRule });

        var outDir = "/runs/out-selected";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(
            Path.Combine(outDir, "outputs.json"),
            "{ \"lead\": \"[\\\"tdd_spirit\\\",\\\"clean_interfaces\\\"]\" }");
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        Assert.Equal(new[] { "tdd_spirit", "clean_interfaces" }, capturingRule.LastContext?.SelectedPractices);
    }

    [Fact]
    public async Task Reads_AllPractices_From_Bundle()
    {
        var fileSystem = new MockFileSystem();
        var capturingRule = new CapturingRule();
        var evaluatorEngine = new EvaluatorEngine(new[] { capturingRule });

        var outDir = "/runs/out-all-practices";
        fileSystem.Directory.CreateDirectory(outDir);
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        Assert.Equal(
            new[] { "fallback_name", "tdd_spirit" },
            (capturingRule.LastContext?.AllPractices ?? Array.Empty<string>()).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Reads_PreviousRunHasCodeChanges_From_PreviousRunResultsArtifact()
    {
        var fileSystem = new MockFileSystem();
        var capturingRule = new CapturingRule();
        var evaluatorEngine = new EvaluatorEngine(new[] { capturingRule });

        var outDir = "/runs/out-previous-run-result";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(
            Path.Combine(outDir, "previous_run_results.json"),
            "{ \"has_code_changes\": true }");
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        Assert.True(capturingRule.LastContext?.PreviousRunHadCodeChanges);
    }

    [Fact]
    public async Task HardFails_WhenPreviousRunRequiredCodeChanges_AndReplayHasNoDiff()
    {
        var fileSystem = new MockFileSystem();
        var evaluatorEngine = new EvaluatorEngine(new IEvaluatorRule[]
        {
            new ExpectedCodeChangeReplayRule()
        });

        var outDir = "/runs/out-hard-fail";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(
            Path.Combine(outDir, "previous_run_results.json"),
            "{ \"has_code_changes\": true }");
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        var evaluatorPath = Path.Combine(outDir, "evaluator.json");
        var root = JsonDocument.Parse(fileSystem.File.ReadAllText(evaluatorPath)).RootElement;

        Assert.False(root.GetProperty("pass").GetBoolean());
        Assert.Equal(0, root.GetProperty("score").GetDouble());
    }

    [Fact]
    public async Task DoesNotHardFail_WhenPreviousRunExpectationArtifactIsMissing()
    {
        var fileSystem = new MockFileSystem();
        var evaluatorEngine = new EvaluatorEngine(new IEvaluatorRule[]
        {
            new ExpectedCodeChangeReplayRule()
        });

        var outDir = "/runs/out-no-previous-artifact";
        fileSystem.Directory.CreateDirectory(outDir);
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        var evaluatorPath = Path.Combine(outDir, "evaluator.json");
        var root = JsonDocument.Parse(fileSystem.File.ReadAllText(evaluatorPath)).RootElement;

        Assert.True(root.GetProperty("pass").GetBoolean());
    }

    [Fact]
    public async Task Writes_NotesByPractice_For_ConventionMatchedFailedRule()
    {
        var fileSystem = new MockFileSystem();
        var evaluatorEngine = new EvaluatorEngine(new[] { new FixedRule("TDDSpiritRule", passed: false, notes: new[] { "Write tests first." }) });

        var outDir = "/runs/out-matched";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(Path.Combine(outDir, "outputs.json"), "{ \"lead\": \"[\\\"tdd_spirit\\\"]\" }");
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        var root = JsonDocument.Parse(fileSystem.File.ReadAllText(Path.Combine(outDir, "evaluator.json"))).RootElement;
        var notesByPractice = root.GetProperty("practice_attribution").GetProperty("notes_by_practice");

        Assert.True(notesByPractice.TryGetProperty("tdd_spirit", out var notes));
        Assert.Contains("Write tests first.", notes.EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Omits_PracticeNotes_When_UnmatchedRuleFails()
    {
        var fileSystem = new MockFileSystem();
        var evaluatorEngine = new EvaluatorEngine(new[] { new FixedRule("MissingTestsRule", passed: false, notes: new[] { "No tests changed." }) });

        var outDir = "/runs/out-unmatched";
        fileSystem.Directory.CreateDirectory(outDir);
        fileSystem.File.WriteAllText(Path.Combine(outDir, "outputs.json"), "{ \"lead\": \"[\\\"tdd_spirit\\\"]\" }");
        SeedBundle(fileSystem);

        var sut = new PromptOptRunEvaluator(fileSystem, evaluatorEngine);
        await sut.EvaluateAsync(outDir, BundlePath);

        var root = JsonDocument.Parse(fileSystem.File.ReadAllText(Path.Combine(outDir, "evaluator.json"))).RootElement;
        var notesByPractice = root.GetProperty("practice_attribution").GetProperty("notes_by_practice");
        Assert.Empty(notesByPractice.EnumerateObject());
    }

    private sealed class CapturingRule : IEvaluatorRule
    {
        public EvaluationContext? LastContext { get; private set; }

        public RuleResult Evaluate(EvaluationContext context)
        {
            LastContext = context;
            return new RuleResult("capture", true, 0, new string[0]);
        }
    }

    private sealed class FixedRule : IEvaluatorRule
    {
        private readonly string _name;
        private readonly bool _passed;
        private readonly string[] _notes;

        public FixedRule(string name, bool passed, string[] notes)
        {
            _name = name;
            _passed = passed;
            _notes = notes;
        }

        public RuleResult Evaluate(EvaluationContext context)
        {
            return new RuleResult(_name, _passed, 0f, _notes, IsHardFailure: !_passed);
        }
    }

    private static void SeedBundle(MockFileSystem fileSystem)
    {
        fileSystem.Directory.CreateDirectory(Path.Combine(BundlePath, "practices"));
        fileSystem.File.WriteAllText(
            Path.Combine(BundlePath, "practices", "tdd_spirit.md"),
            "---\nName: tdd_spirit\n---\n\ncontent");
        fileSystem.File.WriteAllText(
            Path.Combine(BundlePath, "practices", "fallback_name.md"),
            "content without frontmatter");
    }
}
