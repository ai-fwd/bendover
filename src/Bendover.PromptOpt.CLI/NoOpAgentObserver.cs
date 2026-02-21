using System.Threading.Tasks;
using Bendover.Domain.Interfaces;

namespace Bendover.PromptOpt.CLI;

public class NoOpAgentObserver : IAgentObserver
{
    public Task OnEventAsync(AgentEvent evt)
    {
        return Task.CompletedTask;
    }
}
