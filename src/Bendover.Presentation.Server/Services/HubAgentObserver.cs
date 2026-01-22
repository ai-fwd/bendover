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

    public async Task OnProgressAsync(string message)
    {
        await _hubContext.Clients.All.SendAsync("ReceiveProgress", message);
    }
}
