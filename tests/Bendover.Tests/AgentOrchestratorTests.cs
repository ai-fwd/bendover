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
    private readonly Mock<ILeadAgent> _leadAgentMock;
    private readonly Mock<IChatClientResolver> _clientResolverMock;
    private readonly Mock<IChatClient> _architectClientMock;
    private readonly Mock<IChatClient> _engineerClientMock;
    private readonly Mock<IChatClient> _reviewerClientMock;
    private readonly Mock<IContainerService> _containerServiceMock;
    private readonly Mock<IEnvironmentValidator> _environmentValidatorMock;
    private readonly Mock<IAgentObserver> _observerMock;
    private readonly Mock<IPromptOptRunRecorder> _runRecorderMock;
    private readonly Mock<IPromptOptRunContextAccessor> _runContextAccessorMock;
    private readonly Mock<IGitRunner> _gitRunnerMock;
    private readonly AgentOrchestrator _sut;

    public AgentOrchestratorTests()
    {
        _leadAgentMock = new Mock<ILeadAgent>();
        _clientResolverMock = new Mock<IChatClientResolver>();
        _architectClientMock = new Mock<IChatClient>();
        _engineerClientMock = new Mock<IChatClient>();
        _reviewerClientMock = new Mock<IChatClient>();
        _containerServiceMock = new Mock<IContainerService>();
        _environmentValidatorMock = new Mock<IEnvironmentValidator>();
        _observerMock = new Mock<IAgentObserver>();
        _runRecorderMock = new Mock<IPromptOptRunRecorder>();
        _runContextAccessorMock = new Mock<IPromptOptRunContextAccessor>();
        _gitRunnerMock = new Mock<IGitRunner>();

        _observerMock.Setup(x => x.OnProgressAsync(It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _environmentValidatorMock.Setup(x => x.ValidateAsync())
            .Returns(Task.CompletedTask);
        _containerServiceMock.Setup(x => x.StartContainerAsync())
            .Returns(Task.CompletedTask);
        _containerServiceMock.Setup(x => x.ExecuteScriptAsync(It.IsAny<string>()))
            .ReturnsAsync("ok");
        _containerServiceMock.Setup(x => x.StopContainerAsync())
            .Returns(Task.CompletedTask);

        _runRecorderMock.Setup(x => x.StartRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("test_run_id");
        _runRecorderMock.Setup(x => x.RecordPromptAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(Task.CompletedTask);
        _runRecorderMock.Setup(x => x.RecordOutputAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);
        _runRecorderMock.Setup(x => x.FinalizeRunAsync())
            .Returns(Task.CompletedTask);

        var scriptGen = new ScriptGenerator();

        _clientResolverMock.Setup(x => x.GetClient(AgentRole.Architect)).Returns(_architectClientMock.Object);
        _clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(_engineerClientMock.Object);
        _clientResolverMock.Setup(x => x.GetClient(AgentRole.Reviewer)).Returns(_reviewerClientMock.Object);

        _sut = new AgentOrchestrator(
            _clientResolverMock.Object,
            _containerServiceMock.Object,
            scriptGen,
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
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>()))
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

        _architectClientMock.Verify(x => x.CompleteAsync(
           It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Architect") && m.Text.Contains("tdd_spirit"))),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Once);

        _engineerClientMock.Verify(x => x.CompleteAsync(
           It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Engineer") && m.Text.Contains("tdd_spirit"))),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Once);

        _reviewerClientMock.Verify(x => x.CompleteAsync(
           It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Reviewer"))),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Once);
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
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>()))
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
        _gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>()))
            .ReturnsAsync("abc123");

        // Act
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => _sut.RunAsync(goal, practices));

        // Assert
        Assert.Contains("Lead selected no practices", exception.Message);
        _architectClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _engineerClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        _reviewerClientMock.Verify(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
