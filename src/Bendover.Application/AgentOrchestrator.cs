using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IChatClient _chatClient;
    private readonly IContainerService _containerService;
    private readonly GovernanceEngine _governance;
    private readonly ScriptGenerator _scriptGenerator;
    private readonly IEnvironmentValidator _environmentValidator;

    private readonly IEnumerable<IAgentObserver> _observers;

    public AgentOrchestrator(
        IChatClient chatClient, 
        IContainerService containerService,
        GovernanceEngine governance,
        ScriptGenerator scriptGenerator,
        IEnvironmentValidator environmentValidator,
        IEnumerable<IAgentObserver> observers)
    {
        _chatClient = chatClient;
        _containerService = containerService;
        _governance = governance;
        _scriptGenerator = scriptGenerator;
        _environmentValidator = environmentValidator;
        _observers = observers;
    }

    private async Task NotifyAsync(string message)
    {
        foreach (var observer in _observers)
        {
            await observer.OnProgressAsync(message);
        }
    }

    public async Task RunAsync(string initialGoal)
    {
        // 0. Environment Verification
        await NotifyAsync("Verifying Environment...");
        await _environmentValidator.ValidateAsync();

        // 1. Load Governance Context
        await NotifyAsync("Loading Governance Context...");
        var governanceContext = await _governance.GetContextAsync();

        // 2. Planning Phase
        await NotifyAsync("Planning...");
        // In a real app, we'd loop here based on critique.
        var plan = await _chatClient.CompleteAsync(
            $"You are a Planner. {governanceContext}", 
            $"Goal: {initialGoal}");

        // 3. Critique Phase
        await NotifyAsync("Critiquing Plan...");
        var critique = await _chatClient.CompleteAsync(
            $"You are a Critic. {governanceContext}", 
            $"Review this plan: {plan}");

        // 4. Actor Phase
        await NotifyAsync("Generating Code...");
        var actorCode = await _chatClient.CompleteAsync(
            "You are an Actor. Implement this using the IBendoverSDK.", 
            $"Plan: {plan}. Critique: {critique}");

        // 5. Script Generation
        var script = _scriptGenerator.WrapCode(actorCode);

        // 6. Execution
        await NotifyAsync("Executing in Container...");
        await _containerService.StartContainerAsync();
        try
        {
             await _containerService.ExecuteScriptAsync(script);
        }
        finally
        {
            await _containerService.StopContainerAsync();
        }
        
        await NotifyAsync("Finished.");
    }
}
