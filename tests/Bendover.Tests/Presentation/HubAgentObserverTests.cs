using Bendover.Domain.Interfaces;
using Bendover.Presentation.Server.Hubs;
using Bendover.Presentation.Server.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;

namespace Bendover.Tests.Presentation;

public class HubAgentObserverTests
{
    [Fact]
    public async Task OnEventAsync_ShouldMapProgressEventsToReceiveProgress()
    {
        var observer = CreateObserver(out var clientProxyMock);

        await observer.OnEventAsync(new AgentProgressEvent("Working..."));

        clientProxyMock.Verify(
            x => x.SendCoreAsync(
                "ReceiveProgress",
                It.Is<object?[]>(args => args.Length == 1 && Equals(args[0], "Working...")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnEventAsync_ShouldMapStepEventsToReceiveStep()
    {
        var observer = CreateObserver(out var clientProxyMock);
        var step = new AgentStepEvent(1, "plan", "tool", "observation", false);

        await observer.OnEventAsync(step);

        clientProxyMock.Verify(
            x => x.SendCoreAsync(
                "ReceiveStep",
                It.Is<object?[]>(args => args.Length == 1 && Equals(args[0], step)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task OnEventAsync_ShouldMapTranscriptEventsToReceiveTranscript()
    {
        var observer = CreateObserver(out var clientProxyMock);
        var transcript = new AgentTranscriptEvent("output", "engineer_step_1", "[transcript][output] phase=engineer_step_1 chars=2 preview=ok");

        await observer.OnEventAsync(transcript);

        clientProxyMock.Verify(
            x => x.SendCoreAsync(
                "ReceiveTranscript",
                It.Is<object?[]>(args => args.Length == 1 && Equals(args[0], transcript)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private static HubAgentObserver CreateObserver(out Mock<IClientProxy> clientProxyMock)
    {
        clientProxyMock = new Mock<IClientProxy>();
        var clientsMock = new Mock<IHubClients>();
        var hubContextMock = new Mock<IHubContext<AgentHub>>();

        clientsMock.SetupGet(x => x.All).Returns(clientProxyMock.Object);
        hubContextMock.SetupGet(x => x.Clients).Returns(clientsMock.Object);

        return new HubAgentObserver(hubContextMock.Object);
    }
}
