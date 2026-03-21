using Mystro.Application;
using Mystro.Domain.Interfaces;
using Moq;

namespace Mystro.Tests;

public class AgentEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_ShouldFanOutToAllObservers()
    {
        var observerOne = new Mock<IAgentObserver>();
        var observerTwo = new Mock<IAgentObserver>();
        var publisher = new AgentEventPublisher(new[] { observerOne.Object, observerTwo.Object });
        var evt = new AgentProgressEvent("Working...");

        await publisher.PublishAsync(evt);

        observerOne.Verify(x => x.OnEventAsync(evt), Times.Once);
        observerTwo.Verify(x => x.OnEventAsync(evt), Times.Once);
    }
}
