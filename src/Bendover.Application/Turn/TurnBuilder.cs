using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Turn;

public sealed class TurnBuilder
{
    private readonly Func<Type, TurnCapabilities, TurnStep> _activator;
    private readonly Func<string, Task> _notifyProgressAsync;
    private readonly List<Type> _stepTypes = new();
    private readonly TurnCapabilities _capabilities = new();

    private TurnBuilder(
        Func<Type, TurnCapabilities, TurnStep> activator,
        Func<string, Task> notifyProgressAsync)
    {
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
        _notifyProgressAsync = notifyProgressAsync ?? throw new ArgumentNullException(nameof(notifyProgressAsync));
    }

    public static TurnBuilder Create(
        Func<string, Task> notifyProgressAsync,
        Func<AgentStepEvent, Task> notifyStepAsync,
        IChatClient engineerClient,
        IAgenticTurnService agenticTurnService,
        IPromptOptRunRecorder runRecorder,
        string engineerPromptTemplate)
    {
        ArgumentNullException.ThrowIfNull(notifyProgressAsync);
        ArgumentNullException.ThrowIfNull(notifyStepAsync);
        ArgumentNullException.ThrowIfNull(engineerClient);
        ArgumentNullException.ThrowIfNull(agenticTurnService);
        ArgumentNullException.ThrowIfNull(runRecorder);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineerPromptTemplate);

        return new TurnBuilder(
            (type, capabilities) => CreateTurnStep(
                type,
                capabilities,
                notifyStepAsync,
                engineerClient,
                agenticTurnService,
                runRecorder,
                engineerPromptTemplate),
            notifyProgressAsync);
    }

    public TurnBuilder WithTranscript(bool enabled, IReadOnlyCollection<string> selectedPractices)
    {
        selectedPractices ??= Array.Empty<string>();
        _capabilities.TranscriptWriter = enabled
            ? new StreamingTranscriptWriter(_notifyProgressAsync, selectedPractices)
            : new NoOpTranscriptWriter();
        return this;
    }

    public TurnBuilder WithRunRecording(RunRecordingOptions options)
    {
        _capabilities.RunRecording = options ?? RunRecordingOptions.Default;
        return this;
    }

    public TurnBuilder Add<T>() where T : TurnStep
    {
        _stepTypes.Add(typeof(T));
        return this;
    }

    public TurnDelegate Build()
    {
        TurnDelegate app = _ => Task.CompletedTask;

        for (var i = _stepTypes.Count - 1; i >= 0; i--)
        {
            var stepType = _stepTypes[i];
            var step = _activator(stepType, _capabilities);
            var next = app;
            app = context => step.InvokeAsync(context, next);
        }

        return app;
    }

    private static TurnStep CreateTurnStep(
        Type type,
        TurnCapabilities capabilities,
        Func<AgentStepEvent, Task> notifyStepAsync,
        IChatClient engineerClient,
        IAgenticTurnService agenticTurnService,
        IPromptOptRunRecorder runRecorder,
        string engineerPromptTemplate)
    {
        if (type == typeof(GuardTurnStep))
        {
            return new GuardTurnStep(capabilities, runRecorder);
        }

        if (type == typeof(BuildContextStep))
        {
            return new BuildContextStep();
        }

        if (type == typeof(BuildPromptStep))
        {
            return new BuildPromptStep(engineerPromptTemplate);
        }

        if (type == typeof(InvokeAgentStep))
        {
            return new InvokeAgentStep(capabilities, runRecorder, engineerClient);
        }

        if (type == typeof(ExecuteTurnStep))
        {
            return new ExecuteTurnStep(agenticTurnService);
        }

        if (type == typeof(FinalizeTurnStep))
        {
            return new FinalizeTurnStep(capabilities, runRecorder, notifyStepAsync);
        }

        throw new InvalidOperationException($"Unsupported turn step type: {type.FullName}");
    }
}
