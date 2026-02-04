using Microsoft.AspNetCore.SignalR;
using System;
using System.IO;

namespace Bendover.Presentation.Server.Hubs;

public class AgentHub : Hub
{
    private readonly Bendover.Domain.Interfaces.IAgentOrchestrator _orchestrator;
    private readonly Bendover.Application.Interfaces.IPromptOptRunContextAccessor _runContextAccessor;

    public AgentHub(
        Bendover.Domain.Interfaces.IAgentOrchestrator orchestrator,
        Bendover.Application.Interfaces.IPromptOptRunContextAccessor runContextAccessor)
    {
        _orchestrator = orchestrator;
        _runContextAccessor = runContextAccessor;
    }

    public async Task StartAgent(string goal)
    {
        var runId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var outDir = Path.Combine(".bendover", "promptopt", "runs", runId);
        _runContextAccessor.Current = new Bendover.Application.Interfaces.PromptOptRunContext(
            outDir,
            Capture: true,
            RunId: runId,
            BundleId: "default"
        );

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
