using Microsoft.Extensions.DependencyInjection;

namespace Mystro.Application.Run;

public sealed class RunStageFactory
{
    private readonly IServiceProvider _serviceProvider;

    public RunStageFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public RunStage Create(Type stageType)
    {
        ArgumentNullException.ThrowIfNull(stageType);

        if (!typeof(RunStage).IsAssignableFrom(stageType))
        {
            throw new InvalidOperationException($"Unsupported run stage type: {stageType.FullName}");
        }

        return (RunStage)ActivatorUtilities.CreateInstance(_serviceProvider, stageType);
    }
}
