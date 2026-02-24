using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Threading.Tasks;
using Bendover.Application.Interfaces;
using Bendover.Infrastructure.Services;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class PromptOptRunRecorderTests
{
    private readonly MockFileSystem _fileSystem;
    private readonly PromptOptRunRecorder _sut;
    private readonly Mock<IPromptOptRunContextAccessor> _runContextAccessorMock;

    public PromptOptRunRecorderTests()
    {
        _fileSystem = new MockFileSystem();
        _runContextAccessorMock = new Mock<IPromptOptRunContextAccessor>();

        _sut = new PromptOptRunRecorder(
            _fileSystem,
            _runContextAccessorMock.Object
        );
    }

    [Fact]
    public async Task StartRunAsync_UsesHostOutDirAndWritesMeta_WhenCaptureEnabled()
    {
        // Arrange
        var outDir = "/runs/out-1";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true, RunId: "run-1"));

        // Act
        var runId = await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");

        // Assert
        Assert.Equal("run-1", runId);
        Assert.True(_fileSystem.Directory.Exists(outDir));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "goal.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "base_commit.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "bundle_id.txt")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "run_meta.json")));

        Assert.Equal("Test Goal", _fileSystem.File.ReadAllText(Path.Combine(outDir, "goal.txt")));
    }

    [Fact]
    public async Task FinalizeRunAsync_CaptureEnabled_WritesPromptsAndOutputs()
    {
        // Arrange
        var outDir = "/runs/out-2";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("lead", new List<ChatMessage> { new ChatMessage(ChatRole.User, "Goal: Test Goal") });
        await _sut.RecordOutputAsync("lead", "[\"practice1\"]"); // Simulating serialized output
        await _sut.RecordPromptAsync("architect", new List<ChatMessage> { new ChatMessage(ChatRole.User, "prompt") });
        await _sut.RecordOutputAsync("architect", "plan details");

        // Act
        await _sut.FinalizeRunAsync();

        // Assert
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));
    }

    [Fact]
    public async Task FinalizeRunAsync_CaptureEnabled_WritesTranscriptWithPracticeDeliveryAudit()
    {
        // Arrange
        var outDir = "/runs/out-2-audit";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordOutputAsync("lead", "[\"codebase_overview\", \"tdd_spirit\"]");
        await _sut.RecordPromptAsync(
            "engineer",
            new List<ChatMessage>
            {
                new(ChatRole.System, "Header\n\nSelected Practices:\n- [codebase_overview] (Architecture): desc"),
                new(ChatRole.User, "Plan: Test Goal")
            });
        await _sut.RecordOutputAsync("engineer", "Console.WriteLine(\"hello\");");

        // Act
        await _sut.FinalizeRunAsync();

        // Assert
        var transcriptPath = Path.Combine(outDir, "transcript.md");
        Assert.True(_fileSystem.File.Exists(transcriptPath));
        var transcript = _fileSystem.File.ReadAllText(transcriptPath);

        Assert.Contains("## Practice Delivery Audit", transcript);
        Assert.Contains("| engineer | codebase_overview | yes |", transcript);
        Assert.Contains("| engineer | tdd_spirit | no |", transcript);
        Assert.Contains("### engineer", transcript);
        Assert.Contains("#### Prompt Message 1", transcript);
        Assert.Contains("#### Output", transcript);
    }

    [Fact]
    public async Task RecordOutputAsync_Lead_WritesLiveArtifactsBeforeFinalize()
    {
        var outDir = "/runs/out-live-lead";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("lead", new List<ChatMessage> { new(ChatRole.User, "Goal: Test Goal") });

        await _sut.RecordOutputAsync("lead", "[\"practice1\"]");

        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));
    }

    [Fact]
    public async Task RecordOutputAsync_EngineerStepOutputOnly_DoesNotWriteLiveArtifacts()
    {
        var outDir = "/runs/out-live-no-flush";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("engineer_step_1", new List<ChatMessage> { new(ChatRole.User, "step1") });

        await _sut.RecordOutputAsync("engineer_step_1", "step1 output");

        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));
    }

    [Fact]
    public async Task RecordOutputAsync_Observation_WritesLiveArtifacts()
    {
        var outDir = "/runs/out-live-observation";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("engineer_step_1", new List<ChatMessage> { new(ChatRole.User, "step1") });
        await _sut.RecordOutputAsync("engineer_step_1", "step1 output");

        await _sut.RecordOutputAsync("agentic_step_observation_1", "obs1");

        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));

        var transcript = _fileSystem.File.ReadAllText(Path.Combine(outDir, "transcript.md"));
        Assert.Contains("### engineer_step_1", transcript);
        Assert.Contains("### agentic_step_observation_1", transcript);
    }

    [Fact]
    public async Task FinalizeRunAsync_ObservationOutput_RendersHumanReadableSections_AndPrettyRawJson()
    {
        var outDir = "/runs/out-readable-observation";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordOutputAsync("agentic_step_observation_1", BuildObservationOutput(includeUnparsedStdoutLine: true));
        await _sut.FinalizeRunAsync();

        var transcript = _fileSystem.File.ReadAllText(Path.Combine(outDir, "transcript.md"));
        Assert.Contains("#### Observation Summary", transcript);
        Assert.Contains("completion_signaled: false", transcript);
        Assert.Contains("has_changes: false", transcript);
        Assert.Contains("script_exit: 0", transcript);
        Assert.Contains("diff_exit: -1", transcript);
        Assert.Contains("step_plan: Locate AgentOrchestrator.cs", transcript);
        Assert.Contains("tool_call: sdk.LocateFile(\"AgentOrchestrator.cs\", new LocateFileOptions())", transcript);
        Assert.Contains("#### SDK Actions", transcript);
        Assert.Contains("1. sdk_action_start LocateFile elapsed=0ms", transcript);
        Assert.Contains("2. sdk_action_success LocateFile elapsed=131ms", transcript);
        Assert.Contains("payload:", transcript);
        Assert.Contains("AgentOrchestrator.cs", transcript);
        Assert.Contains("Unparsed stdout lines:", transcript);
        Assert.Contains("raw-non-json-line", transcript);
        Assert.Contains("#### Raw Observation (JSON)", transcript);
        Assert.DoesNotContain("\\u0022event_type\\u0022", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FinalizeRunAsync_ObservationOutput_MalformedJson_FallsBackToLegacyOutput()
    {
        var outDir = "/runs/out-observation-fallback";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordOutputAsync("agentic_step_observation_1", "not-json");
        await _sut.FinalizeRunAsync();

        var transcript = _fileSystem.File.ReadAllText(Path.Combine(outDir, "transcript.md"));
        Assert.Contains("### agentic_step_observation_1", transcript);
        Assert.Contains("#### Output", transcript);
        Assert.Contains("not-json", transcript);
        Assert.DoesNotContain("#### Observation Summary", transcript);
        Assert.DoesNotContain("#### SDK Actions", transcript);
        Assert.DoesNotContain("#### Raw Observation (JSON)", transcript);
    }

    [Fact]
    public async Task FinalizeRunAsync_NonObservationPhase_KeepsLegacyOutputFormat()
    {
        var outDir = "/runs/out-non-observation-format";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordOutputAsync("lead", "[\"practice1\"]");
        await _sut.RecordOutputAsync("engineer_step_1", "Console.WriteLine(\"hello\");");
        await _sut.FinalizeRunAsync();

        var transcript = _fileSystem.File.ReadAllText(Path.Combine(outDir, "transcript.md"));
        var phaseStart = transcript.IndexOf("### engineer_step_1", StringComparison.Ordinal);
        Assert.True(phaseStart >= 0);
        var section = transcript.Substring(phaseStart);
        Assert.Contains("#### Output", section);
        Assert.Contains("Console.WriteLine(\"hello\");", section);
        Assert.DoesNotContain("#### Observation Summary", section);
        Assert.DoesNotContain("#### SDK Actions", section);
        Assert.DoesNotContain("#### Raw Observation (JSON)", section);
    }

    [Fact]
    public async Task RecordOutputAsync_Failure_WritesLiveArtifacts()
    {
        var outDir = "/runs/out-live-failure";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("engineer_step_1", new List<ChatMessage> { new(ChatRole.User, "step1") });
        await _sut.RecordOutputAsync("engineer_step_1", "step1 output");

        await _sut.RecordOutputAsync("engineer_step_failure_1", "failure1");

        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.True(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));

        var transcript = _fileSystem.File.ReadAllText(Path.Combine(outDir, "transcript.md"));
        Assert.Contains("### engineer_step_failure_1", transcript);
        Assert.Contains("failure1", transcript);
    }

    [Fact]
    public async Task RecordArtifactAsync_CaptureEnabled_WritesArtifact()
    {
        var outDir = "/runs/out-artifacts";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordArtifactAsync("git_diff.patch", "diff content");
        await _sut.RecordArtifactAsync("dotnet_build.txt", "build content");
        await _sut.RecordArtifactAsync("dotnet_test_error.txt", "test failed");

        Assert.Equal("diff content", _fileSystem.File.ReadAllText(Path.Combine(outDir, "git_diff.patch")));
        Assert.Equal("build content", _fileSystem.File.ReadAllText(Path.Combine(outDir, "dotnet_build.txt")));
        Assert.Equal("test failed", _fileSystem.File.ReadAllText(Path.Combine(outDir, "dotnet_test_error.txt")));
    }

    [Fact]
    public async Task FinalizeRunAsync_OrdersStepPhases_ForStepProtocol()
    {
        var outDir = "/runs/out-step-order";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("engineer_step_2", new List<ChatMessage> { new(ChatRole.User, "step2") });
        await _sut.RecordOutputAsync("engineer_step_2", "body2");
        await _sut.RecordOutputAsync("agentic_step_observation_1", "obs1");
        await _sut.RecordOutputAsync("engineer_step_failure_1", "fail1");
        await _sut.RecordPromptAsync("engineer_step_1", new List<ChatMessage> { new(ChatRole.User, "step1") });
        await _sut.RecordOutputAsync("engineer_step_1", "body1");
        await _sut.FinalizeRunAsync();

        var transcript = _fileSystem.File.ReadAllText(Path.Combine(outDir, "transcript.md"));

        var step1Index = transcript.IndexOf("### engineer_step_1", StringComparison.Ordinal);
        var obs1Index = transcript.IndexOf("### agentic_step_observation_1", StringComparison.Ordinal);
        var fail1Index = transcript.IndexOf("### engineer_step_failure_1", StringComparison.Ordinal);
        var step2Index = transcript.IndexOf("### engineer_step_2", StringComparison.Ordinal);
        Assert.True(step1Index >= 0);
        Assert.True(obs1Index > step1Index);
        Assert.True(fail1Index > obs1Index);
        Assert.True(step2Index > fail1Index);
    }

    [Fact]
    public async Task FinalizeRunAsync_UsesPromptDeltaForRepeatedEngineerStepPrompts()
    {
        var outDir = "/runs/out-prompt-delta";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: true));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");

        var step1Messages = new List<ChatMessage>
        {
            new(ChatRole.System, "SYSTEM_PROMPT"),
            new(ChatRole.User, "Plan: Test Goal")
        };
        await _sut.RecordPromptAsync("engineer_step_1", step1Messages);
        await _sut.RecordOutputAsync("engineer_step_1", "step1 output");
        await _sut.RecordOutputAsync("agentic_step_observation_1", "obs1");
        await _sut.RecordOutputAsync("engineer_step_failure_1", "failure1");

        var step2Messages = new List<ChatMessage>
        {
            new(ChatRole.System, "SYSTEM_PROMPT"),
            new(ChatRole.User, "Plan: Test Goal"),
            new(ChatRole.User, "Failure digest")
        };
        await _sut.RecordPromptAsync("engineer_step_2", step2Messages);
        await _sut.RecordOutputAsync("engineer_step_2", "step2 output");
        await _sut.RecordOutputAsync("agentic_step_observation_2", "obs2");
        await _sut.RecordOutputAsync("engineer_step_failure_2", "failure2");

        await _sut.FinalizeRunAsync();

        var transcript = _fileSystem.File.ReadAllText(Path.Combine(outDir, "transcript.md"));
        var step2Start = transcript.IndexOf("### engineer_step_2", StringComparison.Ordinal);
        var step2End = transcript.IndexOf("### agentic_step_observation_2", StringComparison.Ordinal);
        Assert.True(step2Start >= 0);
        Assert.True(step2End > step2Start);

        var step2Section = transcript.Substring(step2Start, step2End - step2Start);
        Assert.Contains("#### Prompt Message 3", step2Section);
        Assert.Contains("Failure digest", step2Section);
        Assert.DoesNotContain("SYSTEM_PROMPT", step2Section);
        Assert.DoesNotContain("Plan: Test Goal", step2Section);
    }

    [Fact]
    public async Task StartRunAsync_CaptureDisabled_DoesNotWriteMeta()
    {
        // Arrange
        var outDir = "/runs/out-3";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: false));

        // Act
        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");

        // Assert
        Assert.True(_fileSystem.Directory.Exists(outDir));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "goal.txt")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "base_commit.txt")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "bundle_id.txt")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "run_meta.json")));
    }

    [Fact]
    public async Task FinalizeRunAsync_CaptureDisabled_DoesNotWriteTranscript()
    {
        // Arrange
        var outDir = "/runs/out-4";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: false));

        // Act
        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("lead", new List<ChatMessage> { new(ChatRole.User, "Goal: Test Goal") });
        await _sut.RecordOutputAsync("lead", "[\"practice1\"]");
        await _sut.FinalizeRunAsync();

        // Assert
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));
    }

    [Fact]
    public async Task RecordOutputAsync_CaptureDisabled_DoesNotWriteLiveArtifacts()
    {
        var outDir = "/runs/out-5";
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(outDir, Capture: false));

        await _sut.StartRunAsync("Test Goal", "abc1234", "bundle-1");
        await _sut.RecordPromptAsync("lead", new List<ChatMessage> { new(ChatRole.User, "Goal: Test Goal") });
        await _sut.RecordOutputAsync("lead", "[\"practice1\"]");

        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "prompts.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "outputs.json")));
        Assert.False(_fileSystem.File.Exists(Path.Combine(outDir, "transcript.md")));
    }

    private static string BuildObservationOutput(bool includeUnparsedStdoutLine)
    {
        var eventStart = JsonSerializer.Serialize(new
        {
            event_type = "sdk_action_start",
            method_name = "LocateFile",
            elapsed_ms = 0,
            payload_json = (string?)null,
            error = (object?)null
        });
        var eventSuccess = JsonSerializer.Serialize(new
        {
            event_type = "sdk_action_success",
            method_name = "LocateFile",
            elapsed_ms = 131,
            payload_json = JsonSerializer.Serialize(new
            {
                Pattern = "AgentOrchestrator.cs",
                TotalMatches = 1
            }),
            error = (object?)null
        });

        var stdout = includeUnparsedStdoutLine
            ? $"{eventStart}\n{eventSuccess}\nraw-non-json-line\n"
            : $"{eventStart}\n{eventSuccess}\n";

        var observation = new
        {
            ScriptExecution = new
            {
                ExitCode = 0,
                Stdout = stdout,
                Stderr = "",
                CombinedOutput = stdout
            },
            DiffExecution = new
            {
                ExitCode = -1,
                Stdout = "",
                Stderr = "",
                CombinedOutput = "skipped: diff skipped until sdk.Done() is called"
            },
            HasChanges = false,
            CompletionSignaled = false,
            StepPlan = "Locate AgentOrchestrator.cs",
            ToolCall = "sdk.LocateFile(\"AgentOrchestrator.cs\", new LocateFileOptions())"
        };

        return JsonSerializer.Serialize(observation);
    }
}
