using Bendover.Application;
using Bendover.Domain;

namespace Bendover.Tests;

public class PracticeServiceTests : IDisposable
{
    private readonly string _testDir;
    private readonly PracticeService _sut;

    public PracticeServiceTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDir);

        // Create sample practice
        var practiceContent = @"---
Name: sample_practice
TargetRole: Engineer
AreaOfConcern: Code Style
---
Sample Content";

        File.WriteAllText(Path.Combine(_testDir, "sample.md"), practiceContent);

        _sut = new PracticeService(_testDir);
    }

    [Fact]
    public async Task GetPracticesAsync_ShouldLoadFromFiles()
    {
        var practices = await _sut.GetPracticesAsync();

        Assert.Single(practices);
        var practice = practices.First();
        Assert.Equal("sample_practice", practice.Name);
        Assert.Equal(AgentRole.Engineer, practice.TargetRole);
        Assert.Equal("Code Style", practice.AreaOfConcern);
        Assert.Equal("Sample Content", practice.Content.Trim());
    }

    [Fact]
    public async Task GetPracticesForRoleAsync_ShouldFilterByRole()
    {
        var practices = await _sut.GetPracticesForRoleAsync(AgentRole.Engineer);
        Assert.Single(practices);
        Assert.Equal("sample_practice", practices.First().Name);
    }

    [Fact]
    public async Task GetPracticesForRoleAsync_ShouldReturnEmpty_WhenNoMatch()
    {
        var practices = await _sut.GetPracticesForRoleAsync(AgentRole.Architect);
        Assert.Empty(practices);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }
}
