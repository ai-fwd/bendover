using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class AgentOrchestratorTests
{
    private const string AcceptedPatch = "diff --git a/a.txt b/a.txt\n+line";

    private readonly Mock<IAgentPromptService> _agentPromptServiceMock;
    private readonly Mock<ILeadAgent> _leadAgentMock;
    private readonly Mock<IChatClientResolver> _clientResolverMock;
    private readonly Mock<IChatClient> _engineerClientMock;
    private readonly Mock<IContainerService> _containerServiceMock;
    private readonly Mock<IAgenticTurnService> _agenticTurnServiceMock;
    private readonly Mock<IEnvironmentValidator> _environmentValidatorMock;
    private readonly Mock<IAgentObserver> _observerMock;
    private readonly Mock<IPromptOptRunRecorder> _runRecorderMock;
    private readonly Mock<IPromptOptRunContextAccessor> _runContextAccessorMock;
    private readonly Mock<IGitRunner> _gitRunnerMock;
    private readonly AgentOrchestrator _sut;

    public AgentOrchestratorTests()
    {
        _agentPromptServiceMock = new Mock<IAgentPromptService>();
        _leadAgentMock = new Mock<ILeadAgent>();
        _clientResolverMock = new Mock<IChatClientResolver>();
        _engineerClientMock = new Mock<IChatClient>();
        _containerServiceMock = new Mock<IContainerService>();
        _agenticTurnServiceMock = new Mock<IAgenticTurnService>();
        _environmentValidatorMock = new Mock<IEnvironmentValidator>();
        _observerMock = new Mock<IAgentObserver>();
        _runRecorderMock = new Mock<IPromptOptRunRecorder>();
        _runContextAccessorMock = new Mock<IPromptOptRunContextAccessor>();
        _gitRunnerMock = new Mock<IGitRunner>();

        _observerMock.Setup(x => x.OnProgressAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _environmentValidatorMock.Setup(x => x.ValidateAsync())
            .Returns(Task.CompletedTask);
        _containerServiceMock.Setup(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()))
            .Returns(Task.CompletedTask);
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync(It.IsAny<string>()))
            .ReturnsAsync((string cmd) =>
            {
                if (string.Equals(cmd, "cd /workspace && git diff", StringComparison.Ordinal))
                {
                    return new SandboxExecutionResult(0, AcceptedPatch, string.Empty, AcceptedPatch);
                }

                return new SandboxExecutionResult(0, "ok", string.Empty, "ok");
            });
        _containerServiceMock.Setup(x => x.StopContainerAsync())
            .Returns(Task.CompletedTask);

        _agentPromptServiceMock.Setup(x => x.LoadEngineerPromptTemplate(It.IsAny<string?>()))
            .Returns("Engineer prompt template");

        _runRecorderMock.Setup(x => x.StartRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("test_run_id");
        _runRecorderMock.Setup(x => x.RecordPromptAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(Task.CompletedTask);
        _runRecorderMock.Setup(x => x.RecordOutputAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _runRecorderMock.Setup(x => x.RecordArtifactAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _runRecorderMock.Setup(x => x.FinalizeRunAsync())
            .Returns(Task.CompletedTask);

        _clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(_engineerClientMock.Object);

        _sut = new AgentOrchestrator(
            _agentPromptServiceMock.Object,
            _clientResolverMock.Object,
            _containerServiceMock.Object,
            new ScriptGenerator(),
            _agenticTurnServiceMock.Object,
            _environmentValidatorMock.Object,
            new[] { _observerMock.Object },
            _leadAgentMock.Object,
            _runRecorderMock.Object,
            _runContextAccessorMock.Object,
            _gitRunnerMock.Object);
    }

    [Fact]
    public async Task RunAsync_ShouldCompleteOnDone_EvenWithoutDiffOrBuildPass_AndRecordRunResult()
    {
        SetupRunContext();
        SetupLeadSelection("Build feature");
        SetupEngineerSteps();
        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateCompleteObservation(buildPassed: false, hasChanges: false));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
            .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync("Build feature", CreatePractices());

        _runRecorderMock.Verify(
            x => x.RecordArtifactAsync(
                "run_result.json",
                It.Is<string>(text =>
                    text.Contains("\"status\":\"completed\"", StringComparison.Ordinal) &&
                    text.Contains("\"has_code_changes\":false", StringComparison.Ordinal) &&
                    text.Contains("\"git_diff_bytes\":0", StringComparison.Ordinal))),
            Times.Once);
        _runRecorderMock.Verify(x => x.RecordOutputAsync("engineer_step_failure_1", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldNotResetWorkspaceOrReplayPatchAcrossTurns()
    {
        SetupRunContext();
        SetupLeadSelection("Build feature");
        SetupEngineerSteps();
        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateDiscoveryObservation())
            .ReturnsAsync(CreateCompleteObservation(buildPassed: true, hasChanges: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync("Build feature", CreatePractices());

        _containerServiceMock.Verify(x => x.ResetWorkspaceAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        _containerServiceMock.Verify(x => x.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldRecordFailureDigestAndContinue_WhenScriptExitIsNonZero()
    {
        SetupRunContext();
        SetupLeadSelection("Build feature");
        SetupEngineerSteps();
        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateScriptFailureObservation())
            .ReturnsAsync(CreateCompleteObservation(buildPassed: true, hasChanges: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync("Build feature", CreatePractices());

        _runRecorderMock.Verify(
            x => x.RecordOutputAsync(
                "engineer_step_failure_1",
                It.Is<string>(text => text.Contains("script_exit_non_zero", StringComparison.Ordinal))),
            Times.Once);
        _agenticTurnServiceMock.Verify(
            x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RunAsync_ShouldFeedFailureDigestToNextPrompt()
    {
        SetupRunContext();
        SetupLeadSelection("Build feature");
        var capturedEngineerMessages = new List<IReadOnlyList<ChatMessage>>();
        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IList<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) =>
                capturedEngineerMessages.Add(messages.ToList()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.Shell.Execute(\"pwd\");") }));
        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateScriptFailureObservation())
            .ReturnsAsync(CreateCompleteObservation(buildPassed: true, hasChanges: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync("Build feature", CreatePractices());

        Assert.True(capturedEngineerMessages.Count >= 2);
        var secondStepUserText = string.Join(
            "\n",
            capturedEngineerMessages[1]
                .Where(message => string.Equals(message.Role.Value, ChatRole.User.Value, StringComparison.OrdinalIgnoreCase))
                .Select(message => message.Text ?? string.Empty));
        Assert.Contains("Previous step failed", secondStepUserText, StringComparison.Ordinal);
        Assert.Contains("script_exit_non_zero", secondStepUserText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldFeedObservationDigestToNextPrompt()
    {
        SetupRunContext();
        SetupLeadSelection("Build feature");
        var capturedEngineerMessages = new List<IReadOnlyList<ChatMessage>>();
        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IList<ChatMessage>, ChatOptions, CancellationToken>((messages, _, _) =>
                capturedEngineerMessages.Add(messages.ToList()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.Shell.Execute(\"ls -la\");") }));
        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateDiscoveryObservation("Bendover.sln\nREADME.md\n"))
            .ReturnsAsync(CreateCompleteObservation(buildPassed: true, hasChanges: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync("Build feature", CreatePractices());

        Assert.True(capturedEngineerMessages.Count >= 2);
        var secondStepUserText = string.Join(
            "\n",
            capturedEngineerMessages[1]
                .Where(message => string.Equals(message.Role.Value, ChatRole.User.Value, StringComparison.OrdinalIgnoreCase))
                .Select(message => message.Text ?? string.Empty));
        Assert.Contains("Previous step observation (latest)", secondStepUserText, StringComparison.Ordinal);
        Assert.Contains("action_kind=discovery_shell", secondStepUserText, StringComparison.Ordinal);
        Assert.Contains("Bendover.sln", secondStepUserText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ShouldAllowDiscoveryVerificationAndUnknownActionsWithoutFailure()
    {
        SetupRunContext();
        SetupLeadSelection("Build feature");
        SetupEngineerSteps();
        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateDiscoveryObservation())
            .ReturnsAsync(CreateVerificationObservation(buildPassed: false, hasChanges: false))
            .ReturnsAsync(CreateUnknownObservation())
            .ReturnsAsync(CreateCompleteObservation(buildPassed: false, hasChanges: false));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
            .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync("Build feature", CreatePractices());

        _runRecorderMock.Verify(x => x.RecordOutputAsync("engineer_step_failure_1", It.IsAny<string>()), Times.Never);
        _runRecorderMock.Verify(x => x.RecordOutputAsync("engineer_step_failure_2", It.IsAny<string>()), Times.Never);
        _runRecorderMock.Verify(x => x.RecordOutputAsync("engineer_step_failure_3", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldFailAfterMaxActionSteps_WhenNoStepCompletes()
    {
        SetupRunContext();
        SetupLeadSelection("Build feature");
        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.Shell.Execute(\"cat README.md\");") }));
        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateUnknownObservation());
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync("Build feature", CreatePractices()));

        Assert.Contains("24 action steps", ex.Message, StringComparison.Ordinal);
        _runRecorderMock.Verify(
            x => x.RecordArtifactAsync(
                "run_result.json",
                It.Is<string>(text => text.Contains("\"status\":\"failed_max_turns\"", StringComparison.Ordinal))),
            Times.Once);
        _agenticTurnServiceMock.Verify(
            x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()),
            Times.Exactly(24));
    }

    [Fact]
    public async Task RunAsync_ShouldRecordHostApplyCheckFailure_AndThrow()
    {
        SetupRunContext(applySandboxPatchToSource: true);
        SetupLeadSelection("Build feature");
        SetupEngineerSteps();
        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateCompleteObservation(buildPassed: true, hasChanges: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");
        _gitRunnerMock.Setup(x => x.RunAsync("apply --check --whitespace=nowarn -", It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("check failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync("Build feature", CreatePractices()));

        Assert.Contains("check stage", ex.Message, StringComparison.OrdinalIgnoreCase);
        _runRecorderMock.Verify(
            x => x.RecordArtifactAsync("host_apply_check.txt", It.Is<string>(text => text.Contains("check failed", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldRecordHostApplyApplyFailure_AndThrow()
    {
        SetupRunContext(applySandboxPatchToSource: true);
        SetupLeadSelection("Build feature");
        SetupEngineerSteps();
        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateCompleteObservation(buildPassed: true, hasChanges: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");
        _gitRunnerMock.Setup(x => x.RunAsync("apply --check --whitespace=nowarn -", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("check ok");
        _gitRunnerMock.Setup(x => x.RunAsync("apply --whitespace=nowarn -", It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("apply failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync("Build feature", CreatePractices()));

        Assert.Contains("apply stage", ex.Message, StringComparison.OrdinalIgnoreCase);
        _runRecorderMock.Verify(x => x.RecordArtifactAsync("host_apply_check.txt", "check ok"), Times.Once);
        _runRecorderMock.Verify(
            x => x.RecordArtifactAsync("host_apply_result.txt", It.Is<string>(text => text.Contains("apply failed", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldFailFast_WhenBaseCommitCannotBeResolved()
    {
        SetupRunContext();
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("git failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync("Build feature", CreatePractices()));

        Assert.Contains("Failed to resolve base commit from git HEAD", exception.Message, StringComparison.Ordinal);
        _containerServiceMock.Verify(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldFailFast_WhenLeadReturnsUnknownPractice()
    {
        SetupRunContext();
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");
        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync("Build feature", It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
            .ReturnsAsync(new[] { "missing_practice" });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync("Build feature", CreatePractices()));

        Assert.Contains("missing_practice", exception.Message, StringComparison.Ordinal);
        _engineerClientMock.Verify(
            x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldFailFast_WhenLeadReturnsNoPractices()
    {
        SetupRunContext();
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");
        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync("Build feature", It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
            .ReturnsAsync(Array.Empty<string>());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync("Build feature", CreatePractices()));

        Assert.Contains("Lead selected no practices", exception.Message, StringComparison.Ordinal);
        _engineerClientMock.Verify(
            x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void SetupRunContext(bool applySandboxPatchToSource = false)
    {
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(
                OutDir: "/out",
                Capture: true,
                RunId: "run-1",
                BundleId: "bundle-123",
                ApplySandboxPatchToSource: applySandboxPatchToSource));
    }

    private void SetupLeadSelection(string goal)
    {
        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
            .ReturnsAsync(new[] { "tdd_spirit" });
    }

    private void SetupEngineerSteps()
    {
        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.File.Write(\"a.txt\", \"x\");") }));
    }

    private static List<Practice> CreatePractices()
    {
        return new List<Practice>
        {
            new("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };
    }

    private static AgenticTurnObservation CreateScriptFailureObservation()
    {
        return new AgenticTurnObservation(
            ScriptExecution: new SandboxExecutionResult(1, string.Empty, "boom", "boom"),
            DiffExecution: new SandboxExecutionResult(-1, string.Empty, string.Empty, "skipped"),
            ChangedFilesExecution: new SandboxExecutionResult(-1, string.Empty, string.Empty, "skipped"),
            BuildExecution: new SandboxExecutionResult(-1, string.Empty, string.Empty, "skipped"),
            ChangedFiles: Array.Empty<string>(),
            HasChanges: false,
            BuildPassed: false,
            Action: new AgenticStepAction(AgenticStepActionKind.Unknown));
    }

    private static AgenticTurnObservation CreateVerificationObservation(bool buildPassed, bool hasChanges)
    {
        var buildExitCode = buildPassed ? 0 : 1;
        return new AgenticTurnObservation(
            ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
            DiffExecution: new SandboxExecutionResult(0, hasChanges ? AcceptedPatch : string.Empty, string.Empty, hasChanges ? AcceptedPatch : string.Empty),
            ChangedFilesExecution: new SandboxExecutionResult(0, hasChanges ? "a.txt" : string.Empty, string.Empty, hasChanges ? "a.txt" : string.Empty),
            BuildExecution: new SandboxExecutionResult(buildExitCode, buildPassed ? "verification ok" : "verification failed", string.Empty, buildPassed ? "verification ok" : "verification failed"),
            ChangedFiles: hasChanges ? new[] { "a.txt" } : Array.Empty<string>(),
            HasChanges: hasChanges,
            BuildPassed: buildPassed,
            Action: new AgenticStepAction(AgenticStepActionKind.VerificationBuild, "dotnet build Bendover.sln"));
    }

    private static AgenticTurnObservation CreateCompleteObservation(bool buildPassed, bool hasChanges)
    {
        var buildExitCode = buildPassed ? 0 : 1;
        return new AgenticTurnObservation(
            ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
            DiffExecution: new SandboxExecutionResult(0, hasChanges ? AcceptedPatch : string.Empty, string.Empty, hasChanges ? AcceptedPatch : string.Empty),
            ChangedFilesExecution: new SandboxExecutionResult(0, hasChanges ? "a.txt" : string.Empty, string.Empty, hasChanges ? "a.txt" : string.Empty),
            BuildExecution: new SandboxExecutionResult(buildExitCode, buildPassed ? "build ok" : "build failed", string.Empty, buildPassed ? "build ok" : "build failed"),
            ChangedFiles: hasChanges ? new[] { "a.txt" } : Array.Empty<string>(),
            HasChanges: hasChanges,
            BuildPassed: buildPassed,
            Action: new AgenticStepAction(AgenticStepActionKind.Complete, "sdk.Signal.Done"));
    }

    private static AgenticTurnObservation CreateDiscoveryObservation(string scriptOutput = "")
    {
        return new AgenticTurnObservation(
            ScriptExecution: new SandboxExecutionResult(0, scriptOutput, string.Empty, scriptOutput),
            DiffExecution: new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty),
            ChangedFilesExecution: new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty),
            BuildExecution: new SandboxExecutionResult(-1, string.Empty, string.Empty, "skipped"),
            ChangedFiles: Array.Empty<string>(),
            HasChanges: false,
            BuildPassed: false,
            Action: new AgenticStepAction(AgenticStepActionKind.DiscoveryShell, "ls -la"));
    }

    private static AgenticTurnObservation CreateUnknownObservation()
    {
        return new AgenticTurnObservation(
            ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
            DiffExecution: new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty),
            ChangedFilesExecution: new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty),
            BuildExecution: new SandboxExecutionResult(-1, string.Empty, string.Empty, "skipped"),
            ChangedFiles: Array.Empty<string>(),
            HasChanges: false,
            BuildPassed: false,
            Action: new AgenticStepAction(AgenticStepActionKind.Unknown));
    }
}
