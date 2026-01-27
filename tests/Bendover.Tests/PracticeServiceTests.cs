using Bendover.Application;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Moq;

namespace Bendover.Tests;

public class PracticeServiceTests
{
    private readonly Mock<IFileService> _fileServiceMock;
    private readonly PracticeService _sut;
    private readonly string _practicesPath = "/mock/practices";

    public PracticeServiceTests()
    {
        _fileServiceMock = new Mock<IFileService>();

        // Setup default behavior
        _fileServiceMock.Setup(fs => fs.DirectoryExists(It.IsAny<string>())).Returns(true);
        _fileServiceMock.Setup(fs => fs.GetFiles(It.IsAny<string>(), "*.md"))
            .Returns(new[] { Path.Combine(_practicesPath, "sample.md") });

        var practiceContent = @"---
Name: sample_practice
TargetRole: Engineer
AreaOfConcern: Code Style
---
Sample Content";

        _fileServiceMock.Setup(fs => fs.ReadAllText(It.IsAny<string>())).Returns(practiceContent);

        _sut = new PracticeService(_fileServiceMock.Object, _practicesPath);
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
}
