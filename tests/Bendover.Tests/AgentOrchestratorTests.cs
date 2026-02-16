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
    private const string ToolsPromptContent = "Generated tools content";

    private readonly Mock<IAgentPromptService> _agentPromptServiceMock;
    private readonly Mock<ILeadAgent> _leadAgentMock;
    private readonly Mock<IChatClientResolver> _clientResolverMock;
    private readonly Mock<IChatClient> _architectClientMock;
    private readonly Mock<IChatClient> _engineerClientMock;
    private readonly Mock<IChatClient> _reviewerClientMock;
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
        _architectClientMock = new Mock<IChatClient>();
        _engineerClientMock = new Mock<IChatClient>();
        _reviewerClientMock = new Mock<IChatClient>();
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
            .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
        _containerServiceMock.Setup(x => x.StopContainerAsync())
            .Returns(Task.CompletedTask);
        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: true));
        _agentPromptServiceMock.Setup(x => x.LoadEngineerPromptTemplate())
            .Returns($"Engineer prompt template\n\n{ToolsPromptContent}");

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

        var scriptGen = new ScriptGenerator();

        _clientResolverMock.Setup(x => x.GetClient(AgentRole.Architect)).Returns(_architectClientMock.Object);
        _clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(_engineerClientMock.Object);
        _clientResolverMock.Setup(x => x.GetClient(AgentRole.Reviewer)).Returns(_reviewerClientMock.Object);

        _sut = new AgentOrchestrator(
            _agentPromptServiceMock.Object,
            _clientResolverMock.Object,
            _containerServiceMock.Object,
            scriptGen,
            _agenticTurnServiceMock.Object,
            _environmentValidatorMock.Object,
            new[] { _observerMock.Object },
            _leadAgentMock.Object,
            _runRecorderMock.Object,
            _runContextAccessorMock.Object,
            _gitRunnerMock.Object
        );
    }

    [Fact]
    public async Task RunAsync_ShouldExecuteWorkflowInCorrectOrder()
    {
        // Arrange
        var goal = "Build a login feature";
        var practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "tdd_spirit" });

        _architectClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Plan content") }));

        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Code content") }));

        _reviewerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Critique content") }));

        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        // Act
        await _sut.RunAsync(goal, practices);

        // Assert
        _leadAgentMock.Verify(
            x => x.AnalyzeTaskAsync(
                goal,
                It.Is<IReadOnlyCollection<Practice>>(input => input.Any(p => p.Name == "tdd_spirit"))),
            Times.Once);

        _runRecorderMock.Verify(
            x => x.StartRunAsync(goal, "abc123", "bundle-123"),
            Times.Once);
        _containerServiceMock.Verify(
            x => x.StartContainerAsync(It.Is<SandboxExecutionSettings>(settings =>
                string.Equals(settings.BaseCommit, "abc123", StringComparison.Ordinal)
                && string.Equals(settings.SourceRepositoryPath, Directory.GetCurrentDirectory(), StringComparison.Ordinal))),
            Times.Once);

        _architectClientMock.Verify(x => x.CompleteAsync(
           It.IsAny<IList<ChatMessage>>(),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Never);
        // _architectClientMock.Verify(x => x.CompleteAsync(
        //    It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Architect") && m.Text.Contains("tdd_spirit"))),
        //    It.IsAny<ChatOptions>(),
        //    It.IsAny<CancellationToken>()), Times.Once);

        _engineerClientMock.Verify(x => x.CompleteAsync(
           It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null
                                                            && m.Text.Contains("Engineer prompt template", StringComparison.Ordinal)
                                                            && m.Text.Contains("tdd_spirit", StringComparison.Ordinal)
                                                            && m.Text.Contains(ToolsPromptContent, StringComparison.Ordinal))),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Once);

        _reviewerClientMock.Verify(x => x.CompleteAsync(
           It.IsAny<IList<ChatMessage>>(),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Never);
        // _reviewerClientMock.Verify(x => x.CompleteAsync(
        //    It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Reviewer"))),
        //    It.IsAny<ChatOptions>(),
        //    It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldFailFast_WhenBaseCommitCannotBeResolved()
    {
        // Arrange
        var goal = "Build a login feature";
        var practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ThrowsAsync(new Exception("git failed"));

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync(goal, practices));

        // Assert
        Assert.Contains("Failed to resolve base commit from git HEAD", exception.Message, StringComparison.Ordinal);
        _containerServiceMock.Verify(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldFailFast_WhenLeadReturnsUnknownPractice()
    {
        // Arrange
        var goal = "Build a login feature";
        var practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "missing_practice" });
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync(goal, practices));

        // Assert
        Assert.Contains("missing_practice", exception.Message);
        _architectClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _engineerClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _reviewerClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldFailFast_WhenLeadReturnsNoPractices()
    {
        // Arrange
        var goal = "Build a login feature";
        var practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(Array.Empty<string>());
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync(goal, practices));

        // Assert
        Assert.Contains("Lead selected no practices", exception.Message);
        _architectClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _engineerClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _reviewerClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldRetryEngineer_WhenExecutionFailsThenSucceed()
    {
        // Arrange
        var goal = "Create feature";
        var practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "tdd_spirit" });
        _architectClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Plan content") }));

        _engineerClientMock.SetupSequence(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"bad\"") }))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"good\");") }));

        _reviewerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Looks good") }));

        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(
                scriptExitCode: 1,
                hasChanges: false,
                buildPassed: false,
                scriptOutput: "(1,27): error CS1026: ) expected",
                buildOutput: "Build FAILED"))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: true));

        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        // Act
        await _sut.RunAsync(goal, practices);

        // Assert
        _engineerClientMock.Verify(
            x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        _agenticTurnServiceMock.Verify(
            x => x.ExecuteAgenticTurnAsync("Console.WriteLine(\"good\");", It.IsAny<AgenticTurnSettings>()),
            Times.Once);
        _runRecorderMock.Verify(
            x => x.RecordPromptAsync(
                "engineer_retry_1",
                It.Is<List<ChatMessage>>(messages => messages.Any(m => (m.Text ?? string.Empty).Contains("Failure digest", StringComparison.Ordinal))
                                              && messages.Any(m => (m.Text ?? string.Empty).Contains("script_exit_code=1", StringComparison.Ordinal))
                                              && messages.Any(m => (m.Text ?? string.Empty).Contains("CS1026", StringComparison.Ordinal)))),
            Times.Once);
        _containerServiceMock.Verify(x => x.ExecuteCommandAsync("cat '/workspace/.bendover/agents/tools.md'"), Times.Never);
    }

    [Fact]
    public async Task RunAsync_ShouldRetryEngineer_WhenTurnHasNoChanges()
    {
        var goal = "Create feature";
        var practices = new List<Practice>
        {
            new("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "tdd_spirit" });
        _engineerClientMock.SetupSequence(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"attempt 1\");") }))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"attempt 2\");") }));
        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: false, buildPassed: true))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: true));
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync(goal, practices);

        _agenticTurnServiceMock.Verify(
            x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()),
            Times.Exactly(2));
        _runRecorderMock.Verify(
            x => x.RecordOutputAsync(
                "engineer_failure_digest",
                It.Is<string>(text => text.Contains("empty_diff", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldRetryEngineer_WhenTurnBuildFails()
    {
        var goal = "Create feature";
        var practices = new List<Practice>
        {
            new("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "tdd_spirit" });
        _engineerClientMock.SetupSequence(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"attempt 1\");") }))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"attempt 2\");") }));
        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: false, buildOutput: "Build FAILED"))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: true));
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        await _sut.RunAsync(goal, practices);

        _agenticTurnServiceMock.Verify(
            x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()),
            Times.Exactly(2));
        _runRecorderMock.Verify(
            x => x.RecordOutputAsync(
                "engineer_failure_digest",
                It.Is<string>(text => text.Contains("build_failed", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldFailAfterMaxTurns_WhenNoTurnPassesAcceptanceGates()
    {
        var goal = "Create feature";
        var practices = new List<Practice>
        {
            new("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "tdd_spirit" });
        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"attempt\");") }));
        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: false, buildPassed: true));
        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123"));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync(goal, practices));

        Assert.Contains("failed after 4 turns", ex.Message, StringComparison.Ordinal);
        _agenticTurnServiceMock.Verify(
            x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()),
            Times.Exactly(4));
    }

    [Fact]
    public async Task RunAsync_ShouldStreamCompactTranscript_WhenEnabled()
    {
        // Arrange
        var goal = "Create feature";
        var practices = new List<Practice>
        {
            new Practice("codebase_overview", AgentRole.Architect, "Architecture", "Repo overview.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "codebase_overview" });

        _engineerClientMock.SetupSequence(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"bad\";") }))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"good\");") }));

        _agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(scriptExitCode: 1, hasChanges: false, buildPassed: false, scriptOutput: "error", buildOutput: "build fail"))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: true));

        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext(
                "/out",
                Capture: true,
                RunId: "run-1",
                BundleId: "bundle-123",
                StreamTranscript: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        // Act
        await _sut.RunAsync(goal, practices);

        // Assert
        _observerMock.Verify(
            x => x.OnProgressAsync(It.Is<string>(msg =>
                msg.Contains("[transcript][prompt] phase=engineer", StringComparison.Ordinal))),
            Times.AtLeastOnce);
        _observerMock.Verify(
            x => x.OnProgressAsync(It.Is<string>(msg =>
                msg.Contains("[transcript][output] phase=engineer", StringComparison.Ordinal))),
            Times.AtLeastOnce);
        _observerMock.Verify(
            x => x.OnProgressAsync(It.Is<string>(msg =>
                msg.Contains("[transcript][failure] phase=engineer_failure_digest", StringComparison.Ordinal))),
            Times.AtLeastOnce);
        _observerMock.Verify(
            x => x.OnProgressAsync(It.Is<string>(msg =>
                msg.Contains("[transcript][audit] phase=engineer", StringComparison.Ordinal)
                && msg.Contains("practice=codebase_overview", StringComparison.Ordinal)
                && msg.Contains("delivered=yes", StringComparison.Ordinal))),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task RunAsync_ShouldApplySandboxPatchToSource_WhenEnabled()
    {
        // Arrange
        var goal = "Apply patch";
        var practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "tdd_spirit" });
        _architectClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Plan content") }));
        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"ok\");") }));
        _reviewerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Looks good") }));

        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: true));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt\n+line", string.Empty, "diff --git a/a.txt b/a.txt\n+line"));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet build Bendover.sln"))
            .ReturnsAsync(new SandboxExecutionResult(0, "build ok", string.Empty, "build ok"));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet test"))
            .ReturnsAsync(new SandboxExecutionResult(0, "test ok", string.Empty, "test ok"));

        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123", ApplySandboxPatchToSource: true));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");
        _gitRunnerMock.Setup(x => x.RunAsync("apply --whitespace=nowarn -", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync(string.Empty);

        // Act
        await _sut.RunAsync(goal, practices);

        // Assert
        _gitRunnerMock.Verify(
            x => x.RunAsync(
                "apply --whitespace=nowarn -",
                It.IsAny<string?>(),
                It.Is<string>(stdin => stdin.Contains("diff --git a/a.txt b/a.txt", StringComparison.Ordinal))),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_ShouldNotApplySandboxPatchToSource_WhenDisabled()
    {
        // Arrange
        var goal = "No patch apply";
        var practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
        };

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal, It.IsAny<IReadOnlyCollection<Practice>>()))
            .ReturnsAsync(new[] { "tdd_spirit" });
        _architectClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Plan content") }));
        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"ok\");") }));
        _reviewerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Looks good") }));

        _agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
            .ReturnsAsync(CreateObservation(scriptExitCode: 0, hasChanges: true, buildPassed: true));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
            .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt\n+line", string.Empty, "diff --git a/a.txt b/a.txt\n+line"));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet build Bendover.sln"))
            .ReturnsAsync(new SandboxExecutionResult(0, "build ok", string.Empty, "build ok"));
        _containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet test"))
            .ReturnsAsync(new SandboxExecutionResult(0, "test ok", string.Empty, "test ok"));

        _runContextAccessorMock.Setup(x => x.Current)
            .Returns(new PromptOptRunContext("/out", Capture: true, RunId: "run-1", BundleId: "bundle-123", ApplySandboxPatchToSource: false));
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        // Act
        await _sut.RunAsync(goal, practices);

        // Assert
        _gitRunnerMock.Verify(
            x => x.RunAsync("apply --whitespace=nowarn -", It.IsAny<string?>(), It.IsAny<string?>()),
            Times.Never);
    }

    private static AgenticTurnObservation CreateObservation(
        int scriptExitCode,
        bool hasChanges,
        bool buildPassed,
        string scriptOutput = "ok",
        string diffOutput = "diff --git a/a.txt b/a.txt\n+line",
        string changedFilesOutput = "a.txt",
        string buildOutput = "build ok")
    {
        var normalizedDiffOutput = hasChanges ? diffOutput : string.Empty;
        var normalizedChangedFilesOutput = hasChanges ? changedFilesOutput : string.Empty;
        var buildExitCode = buildPassed ? 0 : 1;

        return new AgenticTurnObservation(
            ScriptExecution: new SandboxExecutionResult(scriptExitCode, scriptOutput, string.Empty, scriptOutput),
            DiffExecution: new SandboxExecutionResult(0, normalizedDiffOutput, string.Empty, normalizedDiffOutput),
            ChangedFilesExecution: new SandboxExecutionResult(0, normalizedChangedFilesOutput, string.Empty, normalizedChangedFilesOutput),
            BuildExecution: new SandboxExecutionResult(buildExitCode, buildOutput, string.Empty, buildOutput),
            ChangedFiles: normalizedChangedFilesOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            HasChanges: hasChanges,
            BuildPassed: buildPassed);
    }
}
