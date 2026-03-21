using Microsoft.Extensions.AI;

namespace Mystro.Domain.Interfaces;

public interface IChatClientResolver
{
    IChatClient GetClient(AgentRole role);
}
