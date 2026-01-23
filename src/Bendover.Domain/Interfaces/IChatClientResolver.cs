using Microsoft.Extensions.AI;

namespace Bendover.Domain.Interfaces;

public interface IChatClientResolver
{
    IChatClient GetClient(AgentRole role);
}
