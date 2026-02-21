using Bendover.Domain.Interfaces;
using Spectre.Console;

namespace Bendover.Presentation.CLI;

public class ConsoleAgentObserver : IAgentObserver
{
    public Task OnEventAsync(AgentEvent evt)
    {
        switch (evt)
        {
            case AgentProgressEvent progress:
                AnsiConsole.MarkupLine($"[grey]Info:[/] {Markup.Escape(progress.Message ?? string.Empty)}");
                break;
            case AgentStepEvent step:
                AnsiConsole.MarkupLine($"[bold]Step #{step.StepNumber}[/]");
                AnsiConsole.MarkupLine($"[#ff8800]Plan:[/] {Markup.Escape(string.IsNullOrWhiteSpace(step.Plan) ? "(not provided)" : step.Plan)}");
                AnsiConsole.MarkupLine($"[yellow]Tool:[/] {Markup.Escape(string.IsNullOrWhiteSpace(step.Tool) ? "(unknown)" : step.Tool)}");
                AnsiConsole.MarkupLine($"[green]Observation:[/] {Markup.Escape(string.IsNullOrWhiteSpace(step.Observation) ? "(none)" : step.Observation)}");
                break;
        }

        return Task.CompletedTask;
    }
}
