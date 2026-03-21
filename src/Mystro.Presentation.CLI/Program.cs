using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Mystro.Domain.Interfaces;
using Mystro.Infrastructure;
using Mystro.Application;
using Mystro.Domain.Exceptions;
using Mystro.Presentation.CLI;

public class Program
{
    public static async Task Main(string[] args)
    {
        var runner = new CliRunner();
        await runner.RunAsync(args);
    }
}
