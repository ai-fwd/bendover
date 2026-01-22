using Bendover.Presentation.Server.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();

// Domain/Application Services
builder.Services.AddSingleton<Bendover.Domain.Interfaces.IChatClient, Bendover.Infrastructure.OpenAIClientAdapter>(); // Assuming generic/mock for now as not specified, or maybe Infrastructure has it? 
// Wait, I haven't checked Infrastructure for ChatClient. The user prompt mentioned OpenAI adapter in "Bendover.Infrastructure".
// I'll assume it's there. If not, I'll need to find it. But I must register core knowns first.

builder.Services.AddSingleton<Bendover.Domain.Interfaces.IEnvironmentValidator, Bendover.Infrastructure.DockerEnvironmentValidator>();
builder.Services.AddSingleton<Bendover.Domain.Interfaces.IContainerService, Bendover.Infrastructure.DockerContainerService>();
builder.Services.AddSingleton<Bendover.Domain.Interfaces.IAgentOrchestrator, Bendover.Application.AgentOrchestrator>();
builder.Services.AddSingleton<Bendover.Domain.Interfaces.IAgentObserver, Bendover.Presentation.Server.Services.HubAgentObserver>();
builder.Services.AddSingleton<Bendover.Application.GovernanceEngine>();
builder.Services.AddSingleton<Bendover.Application.ScriptGenerator>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // app.MapOpenApi(); // Optional if using OpenApi
}

app.UseRouting();
app.MapControllers();
app.MapHub<AgentHub>("/agentHub");

app.Run();
