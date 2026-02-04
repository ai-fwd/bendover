using System;
using System.IO;
using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Bendover.PromptOpt.CLI;

public interface IAgentOrchestratorFactory
{
    IAgentOrchestrator Create(string practicesPath);
}

public class PromptOptAgentOrchestratorFactory : IAgentOrchestratorFactory
{
    private readonly IServiceProvider _serviceProvider;

    public PromptOptAgentOrchestratorFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public IAgentOrchestrator Create(string practicesPath)
    {
        var clientResolver = _serviceProvider.GetRequiredService<IChatClientResolver>();
        var containerService = _serviceProvider.GetRequiredService<IContainerService>();
        var scriptGenerator = _serviceProvider.GetRequiredService<ScriptGenerator>();
        var environmentValidator = _serviceProvider.GetRequiredService<IEnvironmentValidator>();
        var observers = _serviceProvider.GetRequiredService<IEnumerable<IAgentObserver>>();
        var leadAgent = _serviceProvider.GetRequiredService<ILeadAgent>();
        var fileService = _serviceProvider.GetRequiredService<IFileService>();
        var runRecorder = _serviceProvider.GetRequiredService<IPromptOptRunRecorder>();
        var gitRunner = _serviceProvider.GetRequiredService<IGitRunner>();

        var practiceService = new PracticeService(fileService, practicesPath);
        var bundleResolver = new PromptBundleResolver(Directory.GetCurrentDirectory());

        return new AgentOrchestrator(
            clientResolver,
            containerService,
            scriptGenerator,
            environmentValidator,
            observers,
            leadAgent,
            practiceService,
            runRecorder,
            bundleResolver,
            gitRunner
        );
    }
}
