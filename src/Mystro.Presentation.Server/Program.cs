using Mystro.Application;
using Mystro.Application.Interfaces;
using Mystro.Application.Run;
using Mystro.Application.Run.Stages;
using Mystro.Application.Turn;
using Mystro.Domain;
using Mystro.Domain.Interfaces;
using Mystro.Infrastructure;
using Mystro.Infrastructure.Configuration;
using Mystro.Infrastructure.Services;
using Mystro.Presentation.Server.Hubs;
using Mystro.Presentation.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<IChatClientResolver, ChatClientResolver>();
builder.Services.AddSingleton<IAgentPromptService, AgentPromptService>();
builder.Services.AddSingleton<IEnvironmentValidator, DockerEnvironmentValidator>();
builder.Services.AddSingleton<IContainerService, DockerContainerService>();
builder.Services.AddSingleton<IAgenticTurnService, AgenticTurnService>();
builder.Services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
builder.Services.AddSingleton<IAgentEventPublisher, AgentEventPublisher>();
builder.Services.AddSingleton<IAgentObserver, HubAgentObserver>();
builder.Services.AddSingleton<ILeadAgent, LeadAgent>();
builder.Services.AddSingleton<IPracticeService, PracticeService>();
builder.Services.AddSingleton<ScriptGenerator>();
builder.Services.AddSingleton<System.IO.Abstractions.IFileSystem, System.IO.Abstractions.FileSystem>();
builder.Services.AddSingleton<IFileService, FileService>();
builder.Services.AddSingleton<IGitRunner, GitRunner>();
builder.Services.AddSingleton<IDotNetRunner, DotNetRunner>();
builder.Services.AddSingleton<IPromptOptRunContextAccessor, PromptOptRunContextAccessor>();
builder.Services.AddSingleton<IPromptOptRunRecorder, PromptOptRunRecorder>();
builder.Services.AddSingleton<RunStageFactory>();
builder.Services.AddTransient<RepositoryStage>();
builder.Services.AddTransient<RecordingStage>();
builder.Services.AddTransient<SandboxStage>();
builder.Services.AddTransient<PracticeSelectionStage>();
builder.Services.AddSingleton<TurnStepFactory>();
builder.Services.AddTransient<GuardTurnStep>();
builder.Services.AddTransient<BuildContextStep>();
builder.Services.AddTransient<BuildPromptStep>();
builder.Services.AddTransient<InvokeAgentStep>();
builder.Services.AddTransient<ExecuteTurnStep>();
builder.Services.AddTransient<FinalizeTurnStep>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // app.MapOpenApi(); // Optional if using OpenApi
}

app.UseRouting();
app.MapControllers();
app.MapHub<AgentHub>("/agentHub");

app.Run();
