using Mystro.Domain.Entities;
using Mystro.Domain.Interfaces;

namespace Mystro.Application.Run.Stages;

public sealed class SandboxStage : RunStage
{
    private readonly IContainerService _containerService;
    private readonly IEnvironmentValidator _environmentValidator;
    private bool _started;

    public SandboxStage(
        IContainerService containerService,
        IEnvironmentValidator environmentValidator)
    {
        _containerService = containerService;
        _environmentValidator = environmentValidator;
    }

    public override int SetupOrder => 30;

    public override int TeardownOrder => 20;

    public override async Task SetupAsync(RunStageContext context)
    {
        await context.Events.ProgressAsync("Verifying Environment...");
        await _environmentValidator.ValidateAsync();

        await context.Events.ProgressAsync("Executing in Container...");
        await _containerService.StartContainerAsync(new SandboxExecutionSettings(
            context.SourceRepositoryPath,
            BaseCommit: context.BaseCommit));
        _started = true;
    }

    public override async Task TeardownAsync(RunStageContext context)
    {
        if (!_started)
        {
            return;
        }

        await _containerService.StopContainerAsync();
    }
}
