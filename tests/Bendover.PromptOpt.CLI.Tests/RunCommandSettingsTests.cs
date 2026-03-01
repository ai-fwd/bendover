using Spectre.Console.Cli;

namespace Bendover.PromptOpt.CLI.Tests;

public class RunCommandSettingsTests
{
    [Theory]
    [InlineData("live", PromptOptUiMode.Live)]
    [InlineData("LIVE", PromptOptUiMode.Live)]
    [InlineData("plain", PromptOptUiMode.Plain)]
    [InlineData("PLAIN", PromptOptUiMode.Plain)]
    public void GetUiMode_ParsesSupportedValues(string ui, PromptOptUiMode expected)
    {
        var settings = new RunCommandSettings
        {
            Ui = ui
        };

        Assert.True(settings.Validate().Successful);
        Assert.Equal(expected, settings.GetUiMode());
    }

    [Fact]
    public void Validate_Fails_ForUnsupportedUiMode()
    {
        var settings = new RunCommandSettings
        {
            Ui = "unknown"
        };

        var result = settings.Validate();

        Assert.False(result.Successful);
        Assert.Contains("live", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("plain", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
