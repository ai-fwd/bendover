using System;
using System.IO;
using System.Linq;
using Mystro.Application.Interfaces;
using Mystro.Domain;
using Mystro.Domain.Interfaces;
using Microsoft.AspNetCore.SignalR;

namespace Mystro.Presentation.Server.Hubs;

public class AgentHub : Hub
{
    private readonly IAgentOrchestrator _orchestrator;
    private readonly IPracticeService _practiceService;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;

    public AgentHub(
        IAgentOrchestrator orchestrator,
        IPracticeService practiceService,
        IPromptOptRunContextAccessor runContextAccessor)
    {
        _orchestrator = orchestrator;
        _practiceService = practiceService;
        _runContextAccessor = runContextAccessor;
    }

    public async Task StartAgent(string goal)
    {
        var runId = $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}";
        var outDir = Path.Combine(".mystro", "promptopt", "runs", runId);
        _runContextAccessor.Current = new PromptOptRunContext(
            outDir,
            Capture: true,
            RunId: runId,
            BundleId: "default"
        );

        var practices = (await _practiceService.GetPracticesAsync()).ToList();
        await _orchestrator.RunAsync(goal, practices);
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
