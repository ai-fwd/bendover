using System.Reflection;

namespace Mystro.PromptOpt.CLI.Tests;

public class ProgramTests
{
    [Fact]
    public void BuildErrorMessage_ShouldIncludeInnerExceptionDetails()
    {
        var inner = new InvalidOperationException("payload too large");
        var outer = new Exception("top-level failure", inner);
        var method = typeof(RunCommand).GetMethod(
            "BuildErrorMessage",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var message = Assert.IsType<string>(method!.Invoke(null, new object[] { outer }));

        Assert.Contains("Exception: top-level failure", message, StringComparison.Ordinal);
        Assert.Contains("Inner[1] InvalidOperationException: payload too large", message, StringComparison.Ordinal);
    }
}
