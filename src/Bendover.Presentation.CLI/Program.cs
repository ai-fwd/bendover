using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Application;
using Bendover.Domain.Exceptions;
using Bendover.Presentation.CLI;

public class Program
{
    public static async Task Main(string[] args)
    {
        var runner = new CliRunner();
        await runner.RunAsync(args);
    }
}
