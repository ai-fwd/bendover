using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Bendover.Domain;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IChatClient _chatClient;
    private readonly IContainerService _containerService;
    private readonly GovernanceEngine _governance;
    private readonly ScriptGenerator _scriptGenerator;
    private readonly IEnvironmentValidator _environmentValidator;
    private readonly ILeadAgent _leadAgent;
    private readonly IPracticeService _practiceService;

    private readonly IEnumerable<IAgentObserver> _observers;

    public AgentOrchestrator(
        IChatClient chatClient, 
        IContainerService containerService,
        GovernanceEngine governance,
        ScriptGenerator scriptGenerator,
        IEnvironmentValidator environmentValidator,
        IEnumerable<IAgentObserver> observers,
        ILeadAgent leadAgent,
        IPracticeService practiceService)
    {
        _chatClient = chatClient;
        _containerService = containerService;
        _governance = governance;
        _scriptGenerator = scriptGenerator;
        _environmentValidator = environmentValidator;
        _observers = observers;
        _leadAgent = leadAgent;
        _practiceService = practiceService;
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

        // 1a. Lead Phase (Practice Selection)
        await NotifyAsync("Lead Agent Analyzing Request...");
        var selectedPracticeNames = await _leadAgent.AnalyzeTaskAsync(initialGoal);
        var allPractices = await _practiceService.GetPracticesAsync();
        var selectedPractices = allPractices.Where(p => selectedPracticeNames.Contains(p.Name)).ToList();

        // Format practices for prompt
        var practicesContext = string.Join("\n", selectedPractices.Select(p => $"- [{p.Name}] ({p.AreaOfConcern}): {p.Content}"));

        // 2. Planning Phase (Architect)
        await NotifyAsync("Architect Planning...");
        // In a real app, we'd loop here based on critique.
        var plan = await _chatClient.CompleteAsync(
            $"You are an Architect. {governanceContext}\n\nSelected Practices:\n{practicesContext}", 
            $"Goal: {initialGoal}");

        // 3. Actor Phase (Engineer)
        await NotifyAsync("Engineer Generating Code...");
        var actorCode = await _chatClient.CompleteAsync(
            $"You are an Engineer. Implement this using the IBendoverSDK.\n\nSelected Practices:\n{practicesContext}", 
            $"Plan: {plan}");

        // 4. Critique Phase (Reviewer)
        await NotifyAsync("Reviewer Critiquing Code...");
        var critique = await _chatClient.CompleteAsync(
            $"You are a Reviewer. {governanceContext}\n\nSelected Practices:\n{practicesContext}", 
            $"Review this code: {actorCode}");

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
