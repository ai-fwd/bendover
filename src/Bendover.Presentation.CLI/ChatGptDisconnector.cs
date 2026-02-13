using Bendover.Infrastructure.ChatGpt;
using Spectre.Console;

namespace Bendover.Presentation.CLI;

public sealed class ChatGptDisconnector
{
    private readonly ChatGptAuthStore _store;

    public ChatGptDisconnector()
        : this(new ChatGptAuthStore())
    {
    }

    public ChatGptDisconnector(ChatGptAuthStore store)
    {
        _store = store;
    }

    public void Run()
    {
        var hadSession = _store.Load() is not null;
        _store.Clear();

        if (hadSession)
        {
            AnsiConsole.MarkupLine("[green]ChatGPT subscription disconnected.[/]");
            AnsiConsole.MarkupLine($"Removed credentials at [grey]{_store.AuthFilePath}[/].");
            return;
        }

        AnsiConsole.MarkupLine("[yellow]No ChatGPT subscription session found.[/]");
    }
}
