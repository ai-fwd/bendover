using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Microsoft.Extensions.AI;
using Moq;

namespace Bendover.Tests;

public class LeadAgentTests
{
    private readonly ILeadAgent _sut;

    public LeadAgentTests()
    {
        _sut = new FakeLeadAgent();
    }

    [Fact]
    public async Task AnalyzeTaskAsync_ShouldReturnSelectedPractices()
    {
        // Act
        var practices = await _sut.AnalyzeTaskAsync("Build a login feature", Array.Empty<Practice>());

        // Assert
        Assert.NotNull(practices);
        Assert.Contains("tdd_spirit", practices);
        Assert.Contains("clean_interfaces", practices);
        Assert.Equal(2, practices.Count());
    }

    [Fact]
    public async Task AnalyzeTaskAsync_ShouldBuildPromptFromProvidedPractices_AndNormalizeResponse()
    {
        // Arrange
        var resolverMock = new Mock<IChatClientResolver>();
        var leadClientMock = new Mock<IChatClient>();
        IList<ChatMessage>? capturedMessages = null;

        resolverMock.Setup(x => x.GetClient(AgentRole.Lead))
            .Returns(leadClientMock.Object);
        leadClientMock
            .Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback((IList<ChatMessage> messages, ChatOptions _, CancellationToken _) => capturedMessages = messages)
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "[\"tdd_spirit\", \" tdd_spirit \", \"clean_interfaces\"]") }));

        var leadAgent = new Bendover.Application.LeadAgent(resolverMock.Object);
        var practices = new[]
        {
            new Practice("lead_agent_practice", AgentRole.Lead, "Orchestration", "Lead prompt body"),
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first."),
            new Practice("clean_interfaces", AgentRole.Engineer, "Code Style", "Keep interfaces small.")
        };

        // Act
        var selected = (await leadAgent.AnalyzeTaskAsync("Build a login feature", practices)).ToArray();

        // Assert
        Assert.NotNull(capturedMessages);
        Assert.Equal(ChatRole.System.Value, capturedMessages![0].Role.Value);
        Assert.Contains("Lead prompt body", capturedMessages[0].Text);
        Assert.Contains("Name: tdd_spirit", capturedMessages[0].Text);
        Assert.Contains("Name: clean_interfaces", capturedMessages[0].Text);
        Assert.DoesNotContain("Name: lead_agent_practice", capturedMessages[0].Text);

        Assert.Equal(new[] { "tdd_spirit", "clean_interfaces" }, selected);
    }

    [Fact]
    public async Task AnalyzeTaskAsync_ShouldPassUserPromptAsPlainText()
    {
        var resolverMock = new Mock<IChatClientResolver>();
        var leadClientMock = new Mock<IChatClient>();
        IList<ChatMessage>? capturedMessages = null;

        resolverMock.Setup(x => x.GetClient(AgentRole.Lead))
            .Returns(leadClientMock.Object);
        leadClientMock
            .Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback((IList<ChatMessage> messages, ChatOptions _, CancellationToken _) => capturedMessages = messages)
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "[]") }));

        var leadAgent = new Bendover.Application.LeadAgent(resolverMock.Object);
        var userPrompt = "Build a login endpoint with auth validation";

        await leadAgent.AnalyzeTaskAsync(userPrompt, Array.Empty<Practice>());

        Assert.NotNull(capturedMessages);
        Assert.Equal(2, capturedMessages!.Count);
        Assert.Equal(ChatRole.User.Value, capturedMessages[1].Role.Value);
        Assert.Equal(userPrompt, capturedMessages[1].Text);
    }
}
