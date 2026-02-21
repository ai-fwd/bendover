using Bendover.Domain.Interfaces;
using Microsoft.AspNetCore.SignalR.Client;
using Spectre.Console;

namespace Bendover.Presentation.CLI;

public class RemoteAgentRunner : IAgentRunner
{
    public async Task RunAsync(string[] args)
    {
        AnsiConsole.MarkupLine("[bold yellow]Connecting to Remote Agent at http://localhost:5062/agentHub ...[/]");

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost:5062/agentHub")
            .Build();

        var completionSource = new TaskCompletionSource();

        connection.On<string>("ReceiveProgress", (message) =>
        {
            AnsiConsole.MarkupLine($"[grey]Remote:[/] {message}");
            if (message == "Finished.")
            {
                completionSource.TrySetResult();
            }
        });

        connection.On<AgentStepEvent>("ReceiveStep", (step) =>
        {
            AnsiConsole.MarkupLine($"[bold]Remote Step #{step.StepNumber}[/]");
            AnsiConsole.MarkupLine($"[#ff8800]Plan:[/] {Markup.Escape(string.IsNullOrWhiteSpace(step.Plan) ? "(not provided)" : step.Plan)}");
            AnsiConsole.MarkupLine($"[yellow]Tool:[/] {Markup.Escape(string.IsNullOrWhiteSpace(step.Tool) ? "(unknown)" : step.Tool)}");
            AnsiConsole.MarkupLine($"[green]Observation:[/] {Markup.Escape(string.IsNullOrWhiteSpace(step.Observation) ? "(none)" : step.Observation)}");
        });

        connection.On<string>("ReceiveCritique", (message) =>
        {
             AnsiConsole.MarkupLine($"[bold red]Critique:[/] {message}");
        });

        try
        {
            await connection.StartAsync();
            AnsiConsole.MarkupLine("[bold green]Connected.[/]");

            await connection.InvokeAsync("StartAgent", "Self-Improvement");

            await completionSource.Task;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {ex.Message}");
            AnsiConsole.MarkupLine("Make sure the server is running: [bold]dotnet run --project src/Bendover.Presentation.Server[/]");
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }
}
