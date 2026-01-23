using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;
using Bendover.Domain;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IChatClientResolver _clientResolver;
    private readonly IContainerService _containerService;
    private readonly GovernanceEngine _governance;
    private readonly ScriptGenerator _scriptGenerator;
    private readonly IEnvironmentValidator _environmentValidator;
    private readonly ILeadAgent _leadAgent;
    private readonly IPracticeService _practiceService;

    private readonly IEnumerable<IAgentObserver> _observers;

    public AgentOrchestrator(
        IChatClientResolver clientResolver, 
        IContainerService containerService,
        GovernanceEngine governance,
        ScriptGenerator scriptGenerator,
        IEnvironmentValidator environmentValidator,
        IEnumerable<IAgentObserver> observers,
        ILeadAgent leadAgent,
        IPracticeService practiceService)
    {
        _clientResolver = clientResolver;
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
        var architectClient = _clientResolver.GetClient(AgentRole.Architect);
        var planResponse = await architectClient.CompleteAsync(new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, $"You are an Architect. {governanceContext}\n\nSelected Practices:\n{practicesContext}"),
            new ChatMessage(ChatRole.User, $"Goal: {initialGoal}")
        });
        var plan = planResponse.Message.Text;

        // 3. Actor Phase (Engineer)
        await NotifyAsync("Engineer Generating Code...");
        var engineerClient = _clientResolver.GetClient(AgentRole.Engineer);
        var actorCodeResponse = await engineerClient.CompleteAsync(new List<ChatMessage>
        {
            new ChatMessage(ChatRole.System, $"You are an Engineer. Implement this using the IBendoverSDK.\n\nSelected Practices:\n{practicesContext}"),
            new ChatMessage(ChatRole.User, $"Plan: {plan}")
        });
        var actorCode = actorCodeResponse.Message.Text;

        // 4. Critique Phase (Reviewer)
        await NotifyAsync("Reviewer Critiquing Code...");
        var reviewerClient = _clientResolver.GetClient(AgentRole.Reviewer);
        var critiqueResponse = await reviewerClient.CompleteAsync(new List<ChatMessage>
        {
             new ChatMessage(ChatRole.System, $"You are a Reviewer. {governanceContext}\n\nSelected Practices:\n{practicesContext}"),
             new ChatMessage(ChatRole.User, $"Review this code: {actorCode}")
        });
        var critique = critiqueResponse.Message.Text;

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
