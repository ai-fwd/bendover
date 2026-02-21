using Bendover.Domain.Interfaces;
using Bendover.Presentation.Server.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Bendover.Presentation.Server.Services;

public class HubAgentObserver : IAgentObserver
{
    private readonly IHubContext<AgentHub> _hubContext;

    public HubAgentObserver(IHubContext<AgentHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public async Task OnEventAsync(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentProgressEvent progress:
                await _hubContext.Clients.All.SendAsync("ReceiveProgress", progress.Message);
                break;
            case AgentStepEvent step:
                await _hubContext.Clients.All.SendAsync("ReceiveStep", step);
                break;
        }
    }
}
