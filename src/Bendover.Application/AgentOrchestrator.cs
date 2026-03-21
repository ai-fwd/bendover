using Bendover.Application.Interfaces;
using Bendover.Application.Run;
using Bendover.Application.Run.Stages;
using Bendover.Application.Turn;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;

namespace Bendover.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private const int MaxActionSteps = 100;

    private readonly IAgentPromptService _agentPromptService;
    private readonly IChatClientResolver _clientResolver;
    private readonly IAgenticTurnService _agenticTurnService;
    private readonly IContainerService _containerService;
    private readonly IEnvironmentValidator _environmentValidator;
    private readonly ILeadAgent _leadAgent;
    private readonly IPromptOptRunRecorder _runRecorder;
    private readonly IPromptOptRunContextAccessor _runContextAccessor;
    private readonly IGitRunner _gitRunner;
    private readonly RunStageFactory _runStageFactory;
    private readonly TurnStepFactory _turnStepFactory;

    private readonly IEnumerable<IAgentObserver> _observers;

    public AgentOrchestrator(
        IAgentPromptService agentPromptService,
        IChatClientResolver clientResolver,
        IContainerService containerService,
        ScriptGenerator scriptGenerator,
        IAgenticTurnService agenticTurnService,
        IEnvironmentValidator environmentValidator,
        IEnumerable<IAgentObserver> observers,
        ILeadAgent leadAgent,
        IPromptOptRunRecorder runRecorder,
        IPromptOptRunContextAccessor runContextAccessor,
        IGitRunner gitRunner,
        RunStageFactory runStageFactory,
        TurnStepFactory turnStepFactory)
    {
        _agentPromptService = agentPromptService;
        _clientResolver = clientResolver;
        _agenticTurnService = agenticTurnService;
        _containerService = containerService;
        _environmentValidator = environmentValidator;
        _observers = observers;
        _leadAgent = leadAgent;
        _runRecorder = runRecorder;
        _runContextAccessor = runContextAccessor;
        _gitRunner = gitRunner;
        _runStageFactory = runStageFactory;
        _turnStepFactory = turnStepFactory;
    }

    private async Task EmitEventAsync(AgentEvent evt)
    {
        foreach (var observer in _observers)
        {
            await observer.OnEventAsync(evt);
        }
    }

    private Task NotifyProgressAsync(string message)
    {
        return EmitEventAsync(new AgentProgressEvent(message));
    }

    private Task NotifyStepAsync(AgentStepEvent stepEvent)
    {
        return EmitEventAsync(stepEvent);
    }

    public async Task RunAsync(string initialGoal, IReadOnlyCollection<Practice> practices, string? agentsPath = null)
    {
        if (practices is null)
        {
            throw new ArgumentNullException(nameof(practices));
        }

        var context = _runContextAccessor.Current
            ?? throw new InvalidOperationException("PromptOpt run context is not set.");
        var bundleId = context.BundleId
            ?? throw new InvalidOperationException("PromptOpt run context BundleId is not set.");

        var runStageContext = new RunStageContext
        {
            InitialGoal = initialGoal,
            Practices = practices,
            AgentsPath = agentsPath,
            PromptOptRunContext = context,
            BundleId = bundleId,
            SourceRepositoryPath = Directory.GetCurrentDirectory(),
            NotifyProgressAsync = NotifyProgressAsync
        };

        var runPipeline = RunBuilder.Create(_runStageFactory)
            .Add<RepositoryStage>()
            .Add<RecordingStage>()
            .Add<SandboxStage>()
            .Add<PracticeSelectionStage>()
            .ConfigureTranscript(context.StreamTranscript)
            .Build();
        
        await runPipeline(runStageContext, async runContext =>
        {
            var plan = initialGoal;
            var engineerClient = _clientResolver.GetClient(AgentRole.Engineer);
            var engineerPromptTemplate = _agentPromptService.LoadEngineerPromptTemplate(agentsPath);

            var run = new RunContext
            {
                StepFactory = _turnStepFactory,
                TranscriptWriter = runContext.TranscriptWriter,
                RunRecorder = _runRecorder,
                EngineerClient = engineerClient,
                AgenticTurnService = _agenticTurnService,
                NotifyStepAsync = NotifyStepAsync,
                EngineerPromptTemplate = engineerPromptTemplate,
                SelectedPractices = runContext.SelectedPracticeNames
            };

            var runState = new TurnRunState
            {
                StepHistory = new List<TurnHistoryEntry>()
            };

            var turn = TurnBuilder.Create(run)
                .Add<GuardTurnStep>()
                .Add<BuildContextStep>()
                .Add<BuildPromptStep>()
                .Add<InvokeAgentStep>()
                .Add<ExecuteTurnStep>()
                .Add<FinalizeTurnStep>()
                .Build();

            for (var stepIndex = 0; stepIndex < MaxActionSteps; stepIndex++)
            {
                var stepNumber = stepIndex + 1;
                await NotifyProgressAsync($"Engineer step {stepNumber} of {MaxActionSteps}...");

                var turnContext = new TurnContext
                {
                    StepNumber = stepNumber,
                    RunState = runState,
                    Plan = plan ?? string.Empty,
                    PracticesContext = runContext.PracticesContext
                };

                await turn(turnContext);

                if (turnContext.Result.Kind == TurnResultKind.FailedRetryable)
                {
                    continue;
                }

                if (turnContext.Result.Kind == TurnResultKind.Completed)
                {
                    var diffOutput = turnContext.Observation?.DiffExecution.CombinedOutput ?? string.Empty;
                    runContext.RunResult = RunResult.Completed(
                        completionStep: stepNumber,
                        completionSignaled: true,
                        hasCodeChanges: turnContext.Observation?.HasChanges ?? !string.IsNullOrWhiteSpace(diffOutput),
                        gitDiffBytes: diffOutput.Length,
                        lastScriptExitCode: runState.LastScriptExitCode,
                        lastFailureDigest: runState.LastFailureDigest);
                    await NotifyProgressAsync("Finished.");
                    return;
                }

                if (turnContext.Result.Kind == TurnResultKind.FailedTerminal)
                {
                    var failureDigest = turnContext.Result.FailureDigest ?? runState.LastFailureDigest;
                    runContext.RunResult = RunResult.FailedException(
                        lastScriptExitCode: runState.LastScriptExitCode,
                        lastFailureDigest: failureDigest);
                    throw new InvalidOperationException(
                        $"Engineer step {stepNumber} failed.\n{failureDigest}",
                        turnContext.Result.Exception);
                }
            }

            runContext.RunResult = RunResult.FailedMaxTurns(
                lastScriptExitCode: runState.LastScriptExitCode,
                lastFailureDigest: runState.LastFailureDigest);
            throw new InvalidOperationException($"Engineer failed after {MaxActionSteps} action steps.\n{runState.LastFailureDigest}");
        });
    }
}
