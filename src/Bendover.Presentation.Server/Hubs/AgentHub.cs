using Microsoft.AspNetCore.SignalR;

namespace Bendover.Presentation.Server.Hubs;

public class AgentHub : Hub
{
    private readonly Bendover.Domain.Interfaces.IAgentOrchestrator _orchestrator;

    public AgentHub(Bendover.Domain.Interfaces.IAgentOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task StartAgent(string goal)
    {
        await _orchestrator.RunAsync(goal);
    }

    public async Task SendProgress(string message)
    {
        await Clients.All.SendAsync("ReceiveProgress", message);
    }
    
    public async Task SendCritique(string critique)
    {
        await Clients.All.SendAsync("ReceiveCritique", critique);
    }
}
