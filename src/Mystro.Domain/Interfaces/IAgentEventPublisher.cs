namespace Mystro.Domain.Interfaces;

public interface IAgentEventPublisher
{
    Task PublishAsync(AgentEvent evt);
}
