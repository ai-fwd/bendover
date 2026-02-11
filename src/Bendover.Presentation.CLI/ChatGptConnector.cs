using System.Diagnostics;
using Bendover.Infrastructure.ChatGpt;
using Spectre.Console;

namespace Bendover.Presentation.CLI;

public sealed class ChatGptConnector
{
    public async Task RunAsync()
    {
        AnsiConsole.Write(new Rule("[bold blue]/connect[/]").RuleStyle("grey"));

        var oauthClient = new ChatGptOAuthClient();
        var verifier = new ChatGptSessionVerifier();

        try
        {
            AnsiConsole.MarkupLine("[bold]Opening your browser to sign in to ChatGPT...[/]");

            var session = await oauthClient.AuthorizeAsync(
                async authUrl =>
                {
                    if (!TryOpenBrowser(authUrl.ToString()))
                    {
                        AnsiConsole.MarkupLine($"[yellow]Open your browser and sign in to ChatGPT:[/] {authUrl}");
                    }
                    await Task.CompletedTask;
                },
                CancellationToken.None);

            await verifier.VerifyAsync(session, CancellationToken.None);

            var store = new ChatGptAuthStore();
            store.Save(session);

            AnsiConsole.MarkupLine("[green]ChatGPT subscription connected successfully.[/]");
            AnsiConsole.MarkupLine($"Stored credentials in [grey]{store.AuthFilePath}[/].");
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Authorization canceled. No changes were made.[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Unable to connect ChatGPT subscription:[/] {ex.Message}");
        }
    }

    private static bool TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

}
