using Bendover.Domain;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class ChatClientResolverTests
{
    [Fact]
    public void GetClient_ShouldUseDefaultOptions_WhenNoOverrideExists()
    {
        // Arrange
        var defaultOptions = new AgentOptions
        {
            Model = "default-model",
            Endpoint = "http://localhost:1234",
            ApiKey = "default-key"
        };
        var optionsMock = new Mock<IOptions<AgentOptions>>();
        optionsMock.Setup(x => x.Value).Returns(defaultOptions);

        var resolver = new ChatClientResolver(optionsMock.Object);

        // Act
        var client = resolver.GetClient(AgentRole.Lead);

        // Assert
        Assert.NotNull(client);
        var openAiClient = Assert.IsType<OpenAIChatClient>(client);
        // Note: Microsoft.Extensions.AI.OpenAIChatClient does not expose Model as a public property directly in all versions, 
        // but let's check Metadata or assume we can verify it via the client behavior or reflection if needed.
        // Actually, looking at the code, it maps to `Metadata.ModelId` usually.
        Assert.Equal("default-model", client.Metadata.ModelId);
    }

    [Fact]
    public void GetClient_ShouldUseOverrideOptions_WhenOverrideExists()
    {
        // Arrange
        var defaultOptions = new AgentOptions
        {
            Model = "default-model",
            Endpoint = "http://localhost:1234",
            ApiKey = "default-key",
            RoleOverrides = new Dictionary<AgentRole, AgentOptions>
            {
                { 
                    AgentRole.Engineer, 
                    new AgentOptions 
                    { 
                        Model = "engineer-model", 
                        Endpoint = "https://api.openai.com/v1", 
                        ApiKey = "engineer-key" 
                    } 
                }
            }
        };
        var optionsMock = new Mock<IOptions<AgentOptions>>();
        optionsMock.Setup(x => x.Value).Returns(defaultOptions);

        var resolver = new ChatClientResolver(optionsMock.Object);

        // Act
        var client = resolver.GetClient(AgentRole.Engineer);

        // Assert
        Assert.NotNull(client);
        Assert.Equal("engineer-model", client.Metadata.ModelId);
    }

    [Fact]
    public void GetClient_ShouldMergeOptions_WhenPartialOverrideExists()
    {
        // Arrange
        var defaultOptions = new AgentOptions
        {
            Model = "default-model",
            Endpoint = "http://localhost:1234",
            ApiKey = "default-key",
            RoleOverrides = new Dictionary<AgentRole, AgentOptions>
            {
                { 
                    AgentRole.Reviewer, 
                    new AgentOptions 
                    { 
                        Model = "reviewer-model" 
                        // Endpoint and ApiKey should inherit
                    } 
                }
            }
        };
        var optionsMock = new Mock<IOptions<AgentOptions>>();
        optionsMock.Setup(x => x.Value).Returns(defaultOptions);

        var resolver = new ChatClientResolver(optionsMock.Object);

        // Act
        var client = resolver.GetClient(AgentRole.Reviewer);

        // Assert
        Assert.NotNull(client);
        Assert.Equal("reviewer-model", client.Metadata.ModelId);
        // Validating Endpoint inference is harder without checking internal state,
        // but if it didn't throw, it found a valid Endpoint (default).
        // If it was empty, ChatClientResolver logic throws.
    }
    
    [Fact]
    public void GetClient_ShouldThrow_WhenConfigurationIsMissing()
    {
         // Arrange
        var defaultOptions = new AgentOptions
        {
            Model = null, // Invalid
            Endpoint = null,
            ApiKey = "force-local-mode"
        };
        var optionsMock = new Mock<IOptions<AgentOptions>>();
        optionsMock.Setup(x => x.Value).Returns(defaultOptions);

        var resolver = new ChatClientResolver(optionsMock.Object);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => resolver.GetClient(AgentRole.Lead));
    }
}
