namespace Bendover.Domain.Interfaces;

public interface IAgentObserver
{
    Task OnEventAsync(AgentEvent evt);
}
