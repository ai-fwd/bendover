namespace Mystro.Application.Turn;

public sealed class TurnBuilder
{
    private readonly RunContext _runContext;
    private readonly List<Type> _stepTypes = new();

    private TurnBuilder(RunContext runContext)
    {
        _runContext = runContext ?? throw new ArgumentNullException(nameof(runContext));
    }

    public static TurnBuilder Create(RunContext runContext)
    {
        ArgumentNullException.ThrowIfNull(runContext);
        ArgumentNullException.ThrowIfNull(runContext.StepFactory);
        ArgumentNullException.ThrowIfNull(runContext.TranscriptWriter);
        ArgumentNullException.ThrowIfNull(runContext.RunRecorder);
        ArgumentNullException.ThrowIfNull(runContext.EngineerClient);
        ArgumentNullException.ThrowIfNull(runContext.AgenticTurnService);
        ArgumentNullException.ThrowIfNull(runContext.Events);
        ArgumentException.ThrowIfNullOrWhiteSpace(runContext.EngineerPromptTemplate);
        ArgumentNullException.ThrowIfNull(runContext.SelectedPractices);

        return new TurnBuilder(runContext);
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
            var step = _runContext.StepFactory.Create(stepType, _runContext);
            var next = app;
            app = context => step.InvokeAsync(context, next);
        }

        return app;
    }
}
