using Bendover.Application;
using Bendover.Application.Evaluation;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Configuration;
using Bendover.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bendover.PromptOpt.CLI;

public static class ProgramServiceRegistration
{
    public static void RegisterServices(IServiceCollection services, IConfiguration configuration, string currentDirectory)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();
        services.AddSingleton<IEnvironmentValidator, DockerEnvironmentValidator>();
        services.AddSingleton<IContainerService, DockerContainerService>();
        services.AddSingleton<IAgentOrchestratorFactory, PromptOptAgentOrchestratorFactory>();
        services.AddSingleton<ScriptGenerator>();
        services.AddSingleton<IAgentObserver, NoOpAgentObserver>();
        services.AddSingleton<System.IO.Abstractions.IFileSystem, System.IO.Abstractions.FileSystem>();
        services.AddSingleton<IFileService, FileService>();
        services.AddSingleton<ILeadAgent, LeadAgent>();
        services.AddSingleton<IPracticeService, PracticeService>();
        services.AddSingleton<IGitRunner, GitRunner>();
        services.AddSingleton<IDotNetRunner, DotNetRunner>();
        services.AddSingleton<IPromptBundleResolver>(_ => new PromptBundleResolver(currentDirectory));
        services.AddSingleton<EvaluatorEngine>();
        RegisterEvaluatorRules(services);
        services.AddSingleton<IPromptOptRunContextAccessor, PromptOptRunContextAccessor>();
        services.AddSingleton<IPromptOptRunRecorder, PromptOptRunRecorder>();
        services.AddSingleton<IPromptOptRunEvaluator, PromptOptRunEvaluator>();
    }

    private static void RegisterEvaluatorRules(IServiceCollection services)
    {
        var ruleTypes = typeof(ProgramServiceRegistration).Assembly
            .GetTypes()
            .Where(t =>
                typeof(IEvaluatorRule).IsAssignableFrom(t)
                && t.IsClass
                && !t.IsAbstract);

        foreach (var ruleType in ruleTypes)
        {
            services.AddSingleton(typeof(IEvaluatorRule), ruleType);
        }
    }
}
