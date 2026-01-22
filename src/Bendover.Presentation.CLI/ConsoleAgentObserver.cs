using Bendover.Domain.Interfaces;
using Spectre.Console;

namespace Bendover.Presentation.CLI;

public class ConsoleAgentObserver : IAgentObserver
{
    public Task OnProgressAsync(string message)
    {
        AnsiConsole.MarkupLine($"[grey]Info:[/] {message}");
        return Task.CompletedTask;
    }
}
