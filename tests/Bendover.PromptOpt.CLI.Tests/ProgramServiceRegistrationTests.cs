using Bendover.Application.Evaluation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bendover.PromptOpt.CLI.Tests;

public class ProgramServiceRegistrationTests
{
    [Fact]
    public void Program_Registers_IEvaluatorRule_Implementations()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        ProgramServiceRegistration.RegisterServices(
            services,
            configuration,
            Directory.GetCurrentDirectory());

        using var serviceProvider = services.BuildServiceProvider();
        var rules = serviceProvider.GetServices<IEvaluatorRule>().ToList();

        Assert.Contains(rules, r => r.GetType().Name == "MissingTestsRule");
        Assert.Contains(rules, r => r.GetType().Name == "TestFailureRule");
        Assert.Contains(rules, r => r.GetType().Name == "SingleImplInterfaceRule");
        Assert.Contains(rules, r => r.GetType().Name == "ForbiddenFilesRule");
    }
}
