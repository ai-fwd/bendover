using System.Text.Json;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IChatClientResolver _clientResolver;
    private readonly IContainerService _containerService;
    private readonly ScriptGenerator _scriptGenerator;
    private readonly IEnvironmentValidator _environmentValidator;
    private readonly ILeadAgent _leadAgent;
    private readonly IPromptOptRunRecorder _runRecorder;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;
    private readonly IGitRunner _gitRunner;

    private readonly IEnumerable<IAgentObserver> _observers;

    public AgentOrchestrator(
        IChatClientResolver clientResolver,
        IContainerService containerService,
        ScriptGenerator scriptGenerator,
        IEnvironmentValidator environmentValidator,
        IEnumerable<IAgentObserver> observers,
        ILeadAgent leadAgent,
        IPromptOptRunRecorder runRecorder,
        IPromptOptRunContextAccessor runContextAccessor,
        IGitRunner gitRunner)
    {
        _clientResolver = clientResolver;
        _containerService = containerService;
        _scriptGenerator = scriptGenerator;
        _environmentValidator = environmentValidator;
        _observers = observers;
        _leadAgent = leadAgent;
        _runRecorder = runRecorder;
        _runContextAccessor = runContextAccessor;
        _gitRunner = gitRunner;
    }

    private async Task NotifyAsync(string message)
    {
        foreach (var observer in _observers)
        {
            await observer.OnProgressAsync(message);
        }
    }

    public async Task RunAsync(string initialGoal, IReadOnlyCollection<Practice> practices)
    {
        if (practices is null)
        {
            throw new ArgumentNullException(nameof(practices));
        }

        var allPractices = practices.ToList();
        var availablePracticeNames = new HashSet<string>(
            allPractices.Select(p => p.Name),
            StringComparer.OrdinalIgnoreCase);

        // Capture Run Start
        string baseCommit = "unknown";
        try
        {
            baseCommit = (await _gitRunner.RunAsync("rev-parse HEAD")).Trim();
        }
        catch { }

        var context = _runContextAccessor.Current
            ?? throw new InvalidOperationException("PromptOpt run context is not set.");
        var bundleId = context.BundleId
            ?? throw new InvalidOperationException("PromptOpt run context BundleId is not set.");

        await _runRecorder.StartRunAsync(initialGoal, baseCommit, bundleId);

        try
        {
            // 0. Environment Verification
            await NotifyAsync("Verifying Environment...");
            await _environmentValidator.ValidateAsync();

            // 1a. Lead Phase (Practice Selection)
            await NotifyAsync("Lead Agent Analyzing Request...");
            var selectedPracticeNames = (await _leadAgent.AnalyzeTaskAsync(initialGoal, allPractices)).ToArray();
            var unknownSelectedPractices = selectedPracticeNames
                .Where(name => !availablePracticeNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Record Lead Input
            var leadMessages = new List<ChatMessage> { new ChatMessage(ChatRole.User, $"Goal: {initialGoal}") };
            await _runRecorder.RecordPromptAsync("lead", leadMessages);

            // Output is the list of selected practices
            // Serialization logic is implicit here but for recording output we likely want the string representation
            await _runRecorder.RecordOutputAsync("lead", JsonSerializer.Serialize(selectedPracticeNames));

            if (selectedPracticeNames.Length == 0)
            {
                var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    $"Lead selected no practices. Available practices: [{availableCsv}]");
            }

            if (unknownSelectedPractices.Length > 0)
            {
                var unknownCsv = string.Join(", ", unknownSelectedPractices);
                var availableCsv = string.Join(", ", availablePracticeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                throw new InvalidOperationException(
                    $"Lead selected unknown practices: [{unknownCsv}]. Available practices: [{availableCsv}]");
            }

            var selectedNameSet = new HashSet<string>(selectedPracticeNames, StringComparer.OrdinalIgnoreCase);
            var selectedPractices = allPractices.Where(p => selectedNameSet.Contains(p.Name)).ToList();

            // Format practices for prompt
            var practicesContext = string.Join("\n", selectedPractices.Select(p => $"- [{p.Name}] ({p.AreaOfConcern}): {p.Content}"));

            // Removed RecordPracticesAsync call

            // 2. Planning Phase (Architect)
            await NotifyAsync("Architect Planning...");
            var architectClient = _clientResolver.GetClient(AgentRole.Architect);
            var architectMessages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, $"You are an Architect.\n\nSelected Practices:\n{practicesContext}"),
                new ChatMessage(ChatRole.User, $"Goal: {initialGoal}")
            };
            await _runRecorder.RecordPromptAsync("architect", architectMessages);

            var planResponse = await architectClient.CompleteAsync(architectMessages);
            var plan = planResponse.Message.Text;
            await _runRecorder.RecordOutputAsync("architect", plan ?? "");

            // 3. Actor Phase (Engineer)
            await NotifyAsync("Engineer Generating Code...");
            var engineerClient = _clientResolver.GetClient(AgentRole.Engineer);
            var engineerMessages = new List<ChatMessage>
            {
                new ChatMessage(ChatRole.System, $"You are an Engineer. Implement this using the IBendoverSDK.\n\nSelected Practices:\n{practicesContext}"),
                new ChatMessage(ChatRole.User, $"Plan: {plan}")
            };
            await _runRecorder.RecordPromptAsync("engineer", engineerMessages);

            var actorCodeResponse = await engineerClient.CompleteAsync(engineerMessages);
            var actorCode = actorCodeResponse.Message.Text ?? string.Empty;
            await _runRecorder.RecordOutputAsync("engineer", actorCode);

            // 4. Critique Phase (Reviewer)
            await NotifyAsync("Reviewer Critiquing Code...");
            var reviewerClient = _clientResolver.GetClient(AgentRole.Reviewer);
            var reviewerMessages = new List<ChatMessage>
            {
                 new ChatMessage(ChatRole.System, $"You are a Reviewer.\n\nSelected Practices:\n{practicesContext}"),
                 new ChatMessage(ChatRole.User, $"Review this code: {actorCode}")
            };
            await _runRecorder.RecordPromptAsync("reviewer", reviewerMessages);

            var critiqueResponse = await reviewerClient.CompleteAsync(reviewerMessages);
            var critique = critiqueResponse.Message.Text;
            await _runRecorder.RecordOutputAsync("reviewer", critique ?? "");

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
        finally
        {
            await _runRecorder.FinalizeRunAsync();
        }
    }
}
