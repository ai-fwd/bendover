using Mystro.Application;
using Mystro.Application.Evaluation;
using Mystro.Application.Interfaces;
using Mystro.Application.Run;
using Mystro.Application.Run.Stages;
using Mystro.Application.Turn;
using Mystro.Domain;
using Mystro.Domain.Interfaces;
using Mystro.Infrastructure;
using Mystro.Infrastructure.Configuration;
using Mystro.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Mystro.PromptOpt.CLI;

public static class ProgramServiceRegistration
{
    public static void RegisterServices(IServiceCollection services, IConfiguration configuration, string currentDirectory)
    {
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();
        services.AddSingleton<IAgentPromptService, AgentPromptService>();
        services.AddSingleton<IEnvironmentValidator, DockerEnvironmentValidator>();
        services.AddSingleton<IContainerService, DockerContainerService>();
        services.AddSingleton<IAgenticTurnService, AgenticTurnService>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<IAgentEventPublisher, AgentEventPublisher>();
        services.AddSingleton<ScriptGenerator>();
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
        services.AddSingleton<RunStageFactory>();
        services.AddTransient<RepositoryStage>();
        services.AddTransient<RecordingStage>();
        services.AddTransient<SandboxStage>();
        services.AddTransient<PracticeSelectionStage>();
        services.AddSingleton<TurnStepFactory>();
        services.AddTransient<GuardTurnStep>();
        services.AddTransient<BuildContextStep>();
        services.AddTransient<BuildPromptStep>();
        services.AddTransient<InvokeAgentStep>();
        services.AddTransient<ExecuteTurnStep>();
        services.AddTransient<FinalizeTurnStep>();
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
