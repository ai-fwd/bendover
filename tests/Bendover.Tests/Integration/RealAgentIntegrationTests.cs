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
    [Fact]
    public async Task CanConnectToLocalLLM()
    {
        // Skip if not running locally or if explicit env var not set?
        // For now, we will try to connect. If it fails due to Connection Refused, we might want to fail the test 
        // OR skip it. The user asked for "Hit the localLLM".
        // Use Xunit.SkippableFact if available, or just Fact.
        // Since I don't have SkippableFact without the package, I'll use Fact and catch exception to specific message or just let it fail if the user expects it to pass (assuming they have it running).
        // I'll stick to Fact.
        
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
        
        try 
        {
            var response = await client.CompleteAsync("Hello, are you there?");
            
            // Assert
            Assert.NotNull(response.Message.Text);
            Assert.NotEmpty(response.Message.Text);
        }
        catch (HttpRequestException ex)
        {
             // If we can't connect, that's expected if the server isn't running. 
             // But the test exists. I will mark it as skipped or output a warning if possible, but Xunit is strict.
             // I'll rethrow so the user knows it failed (Validation Plan).
             throw new Exception("Could not connect to Local LLM at 127.0.0.1:1234. Make sure LM Studio or similar is running.", ex);
        }
    }
}
