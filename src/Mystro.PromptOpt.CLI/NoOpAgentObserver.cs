using System.Threading.Tasks;
using Mystro.Domain.Interfaces;

namespace Mystro.PromptOpt.CLI;

public class NoOpAgentObserver : IAgentObserver
{
    public Task OnEventAsync(AgentEvent evt)
    {
        return Task.CompletedTask;
    }
}
