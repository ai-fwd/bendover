using Bendover.Infrastructure.ChatGpt;
using DotNetEnv;
using Spectre.Console.Cli;

namespace Bendover.PromptOpt.CLI;

public sealed class ServeChatGptProxyCommand : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        Env.TraversePath().Load();
        var reflectionModel = (Environment.GetEnvironmentVariable("DSPY_REFLECTION_MODEL") ?? string.Empty).Trim();
        var hasReflectionModel = !string.IsNullOrWhiteSpace(reflectionModel);

        var authStore = new ChatGptAuthStore();
        var tokenProvider = new ChatGptTokenProvider(authStore, new ChatGptOAuthClient());

        await using var proxyServer = new ChatGptOpenAiProxyServer(
            tokenProvider,
            log: message => Console.WriteLine(message));
        proxyServer.Start(cancellationToken);

        Console.WriteLine($"[promptopt] ChatGPT proxy listening at: {proxyServer.BaseApiUrl}");
        if (hasReflectionModel)
        {
            Console.WriteLine($"[promptopt] Reflection model from .env: {reflectionModel}");
        }
        else
        {
            Console.WriteLine("[promptopt] DSPY_REFLECTION_MODEL is not set in .env.");
        }
        Console.WriteLine("[promptopt] Configure reflection env:");
        Console.WriteLine($"[promptopt]   OPENAI_API_BASE={proxyServer.BaseApiUrl}");
        Console.WriteLine("[promptopt]   OPENAI_API_KEY=sk-local-dummy");
        Console.WriteLine($"[promptopt]   DSPY_REFLECTION_MODEL={(hasReflectionModel ? reflectionModel : "openai/<model>")}");
        Console.WriteLine("[promptopt] Request/translation/result logging is enabled by default.");

        if (authStore.Load() is null)
        {
            Console.WriteLine("[promptopt] No ChatGPT session found. Run `./bendover /connect` to authorize.");
        }

        Console.WriteLine("[promptopt] Press Ctrl+C to stop.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler? handler = null;
        handler = (_, args) =>
        {
            args.Cancel = true;
            linkedCts.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            await Task.Delay(Timeout.Infinite, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on Ctrl+C or external cancellation.
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            await proxyServer.StopAsync();
        }

        return 0;
    }
}
