using Bendover.Domain;
using Bendover.Infrastructure;

namespace Bendover.Tests;

public class LeadAgentTests
{
    private readonly ILeadAgent _sut;

    public LeadAgentTests()
    {
        _sut = new FakeLeadAgent();
    }

    [Fact]
    public async Task AnalyzeTaskAsync_ShouldReturnSelectedPractices()
    {
        // Act
        var practices = await _sut.AnalyzeTaskAsync("Build a login feature");

        // Assert
        Assert.NotNull(practices);
        Assert.Contains("tdd_spirit", practices);
        Assert.Contains("clean_interfaces", practices);
        Assert.Equal(2, practices.Count());
    }
}
