using Mystro.Application.Interfaces;
using Mystro.Application.Run;
using Mystro.Application.Run.Stages;
using Mystro.Application.Turn;
using Mystro.Domain;
using Mystro.Domain.Entities;
using Mystro.Domain.Interfaces;

namespace Mystro.Application;

public class AgentOrchestrator : IAgentOrchestrator
{
    private const int MaxActionSteps = 100;
    private enum TurnHandlingAction
    {
        Continue,
        Complete,
        Fail
    }

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
    private readonly IAgentEventPublisher _events;

    public AgentOrchestrator(
        IAgentPromptService agentPromptService,
        IChatClientResolver clientResolver,
        IContainerService containerService,
        ScriptGenerator scriptGenerator,
        IAgenticTurnService agenticTurnService,
        IEnvironmentValidator environmentValidator,
        IAgentEventPublisher events,
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
        _events = events;
        _leadAgent = leadAgent;
        _runRecorder = runRecorder;
        _runContextAccessor = runContextAccessor;
        _gitRunner = gitRunner;
        _runStageFactory = runStageFactory;
        _turnStepFactory = turnStepFactory;
    }

    public async Task RunAsync(string initialGoal, IReadOnlyCollection<Practice> practices, string? agentsPath = null)
    {
        ArgumentNullException.ThrowIfNull(practices);

        var porContext = _runContextAccessor.Current ?? throw new InvalidOperationException("PromptOpt run context is not set.");
        var bundleId = porContext.BundleId ?? throw new InvalidOperationException("PromptOpt run context BundleId is not set.");

        var runStageContext = new RunStageContext
        {
            InitialGoal = initialGoal,
            Practices = practices,
            AgentsPath = agentsPath,
            PromptOptRunContext = porContext,
            BundleId = bundleId,
            SourceRepositoryPath = Directory.GetCurrentDirectory(),
            Events = _events
        };

        var runPipeline = RunBuilder.Create(_runStageFactory)
            .Add<RepositoryStage>()
            .Add<RecordingStage>()
            .Add<SandboxStage>()
            .Add<PracticeSelectionStage>()
            .ConfigureTranscript(porContext.StreamTranscript)
            .Build();

        await runPipeline(runStageContext, async ctx =>
        {
            var plan = initialGoal;
            var engineerClient = _clientResolver.GetClient(AgentRole.Engineer);
            var engineerPromptTemplate = _agentPromptService.LoadEngineerPromptTemplate(agentsPath);

            var runContext = new RunContext
            {
                StepFactory = _turnStepFactory,
                TranscriptWriter = ctx.TranscriptWriter,
                RunRecorder = _runRecorder,
                EngineerClient = engineerClient,
                AgenticTurnService = _agenticTurnService,
                Events = _events,
                EngineerPromptTemplate = engineerPromptTemplate,
                SelectedPractices = ctx.SelectedPracticeNames
            };

            var runState = new TurnRunState
            {
                StepHistory = []
            };

            var turn = TurnBuilder.Create(runContext)
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
                await _events.ProgressAsync($"Engineer step {stepNumber} of {MaxActionSteps}...");

                var turnContext = new TurnContext
                {
                    StepNumber = stepNumber,
                    RunState = runState,
                    Plan = plan ?? string.Empty,
                    PracticesContext = ctx.PracticesContext
                };

                await turn(turnContext);

                var action = HandleTurnResult(
                    turnContext,
                    stepNumber,
                    runState,
                    out var resolvedRunResult,
                    out var exceptionToThrow);

                switch (action)
                {
                    case TurnHandlingAction.Continue:
                        continue;
                    case TurnHandlingAction.Complete:
                        ctx.RunResult = resolvedRunResult!;
                        await _events.ProgressAsync("Finished.");
                        return;
                    case TurnHandlingAction.Fail:
                        ctx.RunResult = resolvedRunResult!;
                        throw exceptionToThrow!;
                    default:
                        throw new InvalidOperationException($"Unsupported turn handling action: {action}");
                }
            }

            ctx.RunResult = RunResult.FailedMaxTurns(
                lastScriptExitCode: runState.LastScriptExitCode,
                lastFailureDigest: runState.LastFailureDigest);
            throw new InvalidOperationException($"Engineer failed after {MaxActionSteps} action steps.\n{runState.LastFailureDigest}");
        });
    }

    private static TurnHandlingAction HandleTurnResult(
        TurnContext turnContext,
        int stepNumber,
        TurnRunState runState,
        out RunResult? runResult,
        out Exception? exceptionToThrow)
    {
        runResult = null;
        exceptionToThrow = null;

        switch (turnContext.Result.Kind)
        {
            case TurnResultKind.FailedRetryable:
            case TurnResultKind.Continue:
                return TurnHandlingAction.Continue;
            case TurnResultKind.Completed:
                var diffOutput = turnContext.Observation?.DiffExecution.CombinedOutput ?? string.Empty;
                runResult = RunResult.Completed(
                    completionStep: stepNumber,
                    completionSignaled: true,
                    hasCodeChanges: turnContext.Observation?.HasChanges ?? !string.IsNullOrWhiteSpace(diffOutput),
                    gitDiffBytes: diffOutput.Length,
                    lastScriptExitCode: runState.LastScriptExitCode,
                    lastFailureDigest: runState.LastFailureDigest);
                return TurnHandlingAction.Complete;
            case TurnResultKind.FailedTerminal:
                var failureDigest = turnContext.Result.FailureDigest ?? runState.LastFailureDigest;
                runResult = RunResult.FailedException(
                    lastScriptExitCode: runState.LastScriptExitCode,
                    lastFailureDigest: failureDigest);
                exceptionToThrow = new InvalidOperationException(
                    $"Engineer step {stepNumber} failed.\n{failureDigest}",
                    turnContext.Result.Exception);
                return TurnHandlingAction.Fail;
            default:
                throw new InvalidOperationException($"Unsupported turn result kind: {turnContext.Result.Kind}");
        }
    }
}
