using Bendover.Application;
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
builder.Services.AddSingleton<IEnvironmentValidator, DockerEnvironmentValidator>();
builder.Services.AddSingleton<IContainerService, DockerContainerService>();
builder.Services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
builder.Services.AddSingleton<IAgentObserver, HubAgentObserver>();
builder.Services.AddSingleton<ILeadAgent, LeadAgent>();
builder.Services.AddSingleton<IPracticeService, PracticeService>();
builder.Services.AddSingleton<ScriptGenerator>();
builder.Services.AddSingleton<System.IO.Abstractions.IFileSystem, System.IO.Abstractions.FileSystem>();
builder.Services.AddSingleton<IGitRunner, GitRunner>();
builder.Services.AddSingleton<IDotNetRunner, DotNetRunner>();
builder.Services.AddSingleton<IPromptOptRunContextAccessor, PromptOptRunContextAccessor>();
builder.Services.AddSingleton<IPromptOptRunRecorder, PromptOptRunRecorder>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    // app.MapOpenApi(); // Optional if using OpenApi
}

app.UseRouting();
app.MapControllers();
app.MapHub<AgentHub>("/agentHub");

app.Run();
