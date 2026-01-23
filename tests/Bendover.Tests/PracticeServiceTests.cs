using Bendover.Application;
using Bendover.Domain;

namespace Bendover.Tests;

public class PracticeServiceTests
{
    private readonly PracticeService _sut;

    public PracticeServiceTests()
    {
        _sut = new PracticeService();
    }

    [Fact]
    public async Task GetPracticesAsync_ShouldReturnMockedPractices()
    {
        // Act
        var practices = await _sut.GetPracticesAsync();

        // Assert
        Assert.NotNull(practices);
        Assert.Equal(3, practices.Count());
        Assert.Contains(practices, p => p.Name == "tdd_spirit");
        Assert.Contains(practices, p => p.Name == "clean_interfaces");
        Assert.Contains(practices, p => p.Name == "readme_hygiene");
    }

    [Fact]
    public async Task GetPracticesForRoleAsync_ShouldFilterByRole()
    {
        // Act
        var engineerPractices = await _sut.GetPracticesForRoleAsync(AgentRole.Engineer);

        // Assert
        Assert.Single(engineerPractices);
        Assert.Equal("clean_interfaces", engineerPractices.First().Name);
        Assert.Equal(AgentRole.Engineer, engineerPractices.First().TargetRole);
    }
}
