using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure;
using Bendover.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;
using Xunit;

namespace Bendover.Tests.Integration;

public class RealAgentIntegrationTests
{
    private const string RealLlmToggleEnvVar = "BENDOVER_INTEGRATION_USE_REAL_LLM";

    [Fact]
    public async Task CanConnectToLocalLLM()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RealLlmToggleEnvVar), "1", StringComparison.Ordinal))
        {
            return;
        }

        // Arrange
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var services = new ServiceCollection();
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.SectionName));
        services.AddSingleton<IChatClientResolver, ChatClientResolver>();

        var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<IChatClientResolver>();

        // Act
        var client = resolver.GetClient(AgentRole.Lead); // Lead uses default or override

        var response = await client.CompleteAsync("Hello, are you there?");

        // Assert
        Assert.NotNull(response.Message.Text);
        Assert.NotEmpty(response.Message.Text);
    }
}
