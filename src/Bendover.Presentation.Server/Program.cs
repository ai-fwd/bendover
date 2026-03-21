using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Application.Run;
using Bendover.Application.Run.Stages;
using Bendover.Application.Turn;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Configuration;
using Bendover.Infrastructure.Services;
using Bendover.Presentation.Server.Hubs;
using Bendover.Presentation.Server.Services;

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
