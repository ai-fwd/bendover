using Microsoft.Extensions.DependencyInjection;

namespace Bendover.Application.Turn;

public sealed class TurnStepFactory
{
    private readonly IServiceProvider _serviceProvider;

    public TurnStepFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public TurnStep Create(Type stepType, RunContext runContext)
    {
        ArgumentNullException.ThrowIfNull(stepType);
        ArgumentNullException.ThrowIfNull(runContext);

        if (!typeof(TurnStep).IsAssignableFrom(stepType))
        {
            throw new InvalidOperationException($"Unsupported turn step type: {stepType.FullName}");
        }

        return (TurnStep)ActivatorUtilities.CreateInstance(_serviceProvider, stepType, runContext);
    }
}
