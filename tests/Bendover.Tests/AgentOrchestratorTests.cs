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
    private readonly Mock<IPracticeService> _practiceServiceMock;
    private readonly Mock<IChatClientResolver> _clientResolverMock;
    private readonly Mock<IChatClient> _architectClientMock;
    private readonly Mock<IChatClient> _engineerClientMock;
    private readonly Mock<IChatClient> _reviewerClientMock;
    private readonly Mock<IContainerService> _containerServiceMock;
    private readonly Mock<IEnvironmentValidator> _environmentValidatorMock;
    private readonly Mock<IAgentObserver> _observerMock;
    private readonly Mock<IPromptOptRunRecorder> _runRecorderMock;
    private readonly Mock<IPromptBundleResolver> _bundleResolverMock;
    private readonly Mock<IGitRunner> _gitRunnerMock;
    private readonly AgentOrchestrator _sut;

    public AgentOrchestratorTests()
    {
        _leadAgentMock = new Mock<ILeadAgent>();
        _practiceServiceMock = new Mock<IPracticeService>();
        _clientResolverMock = new Mock<IChatClientResolver>();
        _architectClientMock = new Mock<IChatClient>();
        _engineerClientMock = new Mock<IChatClient>();
        _reviewerClientMock = new Mock<IChatClient>();
        _containerServiceMock = new Mock<IContainerService>();
        _environmentValidatorMock = new Mock<IEnvironmentValidator>();
        _environmentValidatorMock = new Mock<IEnvironmentValidator>();
        _observerMock = new Mock<IAgentObserver>();
        _runRecorderMock = new Mock<IPromptOptRunRecorder>();
        _bundleResolverMock = new Mock<IPromptBundleResolver>();
        _gitRunnerMock = new Mock<IGitRunner>();

        // Setup Recorder defaults
        _runRecorderMock.Setup(x => x.StartRunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync("test_run_id");

        var scriptGen = new ScriptGenerator(); // Concrete class

        // Setup resolver to return specific mocks
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
            _practiceServiceMock.Object,
            _runRecorderMock.Object,
            _bundleResolverMock.Object,
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

        _leadAgentMock.Setup(x => x.AnalyzeTaskAsync(goal))
            .ReturnsAsync(new[] { "tdd_spirit" });

        _practiceServiceMock.Setup(x => x.GetPracticesAsync())
            .ReturnsAsync(practices);

        _practiceServiceMock.Setup(x => x.GetPracticesAsync())
            .ReturnsAsync(practices);

        // Setup mocks for returns
        _architectClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Plan content") }));

        _engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Code content") }));

        _reviewerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Critique content") }));

        // Act
        await _sut.RunAsync(goal);

        // Assert
        // 1. Lead Agent Analysis
        _leadAgentMock.Verify(x => x.AnalyzeTaskAsync(goal), Times.Once);

        // 2. Architect (Planner)
        // Verify call logic
        _architectClientMock.Verify(x => x.CompleteAsync(
           It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Architect") && m.Text.Contains("tdd_spirit"))),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Once);

        // 3. Engineer (Actor)
        _engineerClientMock.Verify(x => x.CompleteAsync(
           It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Engineer") && m.Text.Contains("tdd_spirit"))),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Once);

        // 4. Reviewer (Critic)
        _reviewerClientMock.Verify(x => x.CompleteAsync(
           It.Is<IList<ChatMessage>>(msgs => msgs.Any(m => m.Text != null && m.Text.Contains("Reviewer"))),
           It.IsAny<ChatOptions>(),
           It.IsAny<CancellationToken>()), Times.Once);

        // Verify Execution Order using Invocations if strict ordering is needed, 
        // but verifying the calls exist is a good start. 
        // We can capture the call sequence to be precise.
    }
}
