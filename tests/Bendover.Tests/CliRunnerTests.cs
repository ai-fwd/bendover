using Xunit;
using Bendover.Presentation.CLI;

namespace Bendover.Tests;

public class CliRunnerTests
{
    [Fact]
    public void GetExecutionMode_ShouldReturnRemote_WhenRemoteFlagIsPresent()
    {
        // Arrange
        var args = new[] { "--remote", "some-goal" };
        var runner = new CliRunner();

        // Act
        var mode = runner.GetExecutionMode(args);

        // Assert
        Assert.Equal(ExecutionMode.Remote, mode);
    }

    [Fact]
    public void GetExecutionMode_ShouldReturnLocal_WhenRemoteFlagIsAbsent()
    {
        // Arrange
        var args = new[] { "some-goal" };
        var runner = new CliRunner();

        // Act
        var mode = runner.GetExecutionMode(args);

        // Assert
        Assert.Equal(ExecutionMode.Local, mode);
    }
}
