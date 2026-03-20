namespace Bendover.Application.Turn;

public sealed class TurnBuilder
{
    private readonly Func<Type, TurnCapabilities, TurnStep> _activator;
    private readonly Func<string, Task> _notifyProgressAsync;
    private readonly List<Type> _stepTypes = new();
    private readonly TurnCapabilities _capabilities = new();

    public TurnBuilder(
        Func<Type, TurnCapabilities, TurnStep> activator,
        Func<string, Task> notifyProgressAsync)
    {
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
        _notifyProgressAsync = notifyProgressAsync ?? throw new ArgumentNullException(nameof(notifyProgressAsync));
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
}
