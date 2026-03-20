namespace Bendover.Application.Turn;

public sealed class TurnBuilder
{
    private readonly Func<Type, TurnStep> _activator;
    private readonly List<Type> _stepTypes = new();

    private TurnBuilder(
        Func<Type, TurnStep> activator)
    {
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
    }

    public static TurnBuilder Create(RunContext runContext)
    {
        ArgumentNullException.ThrowIfNull(runContext);
        ArgumentNullException.ThrowIfNull(runContext.TranscriptWriter);
        ArgumentNullException.ThrowIfNull(runContext.RunRecording);
        ArgumentNullException.ThrowIfNull(runContext.RunRecorder);
        ArgumentNullException.ThrowIfNull(runContext.EngineerClient);
        ArgumentNullException.ThrowIfNull(runContext.AgenticTurnService);
        ArgumentNullException.ThrowIfNull(runContext.NotifyStepAsync);
        ArgumentException.ThrowIfNullOrWhiteSpace(runContext.EngineerPromptTemplate);
        ArgumentNullException.ThrowIfNull(runContext.SelectedPractices);

        return new TurnBuilder(CreateTurnStep);
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
            var step = _activator(stepType);
            var next = app;
            app = context => step.InvokeAsync(context, next);
        }

        return app;
    }

    private static TurnStep CreateTurnStep(Type type)
    {
        if (type == typeof(GuardTurnStep))
        {
            return new GuardTurnStep();
        }

        if (type == typeof(BuildContextStep))
        {
            return new BuildContextStep();
        }

        if (type == typeof(BuildPromptStep))
        {
            return new BuildPromptStep();
        }

        if (type == typeof(InvokeAgentStep))
        {
            return new InvokeAgentStep();
        }

        if (type == typeof(ExecuteTurnStep))
        {
            return new ExecuteTurnStep();
        }

        if (type == typeof(FinalizeTurnStep))
        {
            return new FinalizeTurnStep();
        }

        throw new InvalidOperationException($"Unsupported turn step type: {type.FullName}");
    }
}
