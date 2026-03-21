using Bendover.Domain.Interfaces;

namespace Bendover.Application;

public sealed class AgentEventPublisher : IAgentEventPublisher
{
    private readonly IEnumerable<IAgentObserver> _observers;

    public AgentEventPublisher(IEnumerable<IAgentObserver> observers)
    {
        _observers = observers ?? throw new ArgumentNullException(nameof(observers));
    }

    public async Task PublishAsync(AgentEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        foreach (var observer in _observers)
        {
            await observer.OnEventAsync(evt);
        }
    }
}
