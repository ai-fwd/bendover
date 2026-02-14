using System.Reflection;
using Bendover.Presentation.CLI;
using Xunit;

namespace Bendover.Tests;

public class LocalAgentRunnerArgumentTests
{
    [Fact]
    public void ResolveGoal_ShouldIgnoreTranscriptFlag()
    {
        var goal = InvokeResolveGoal("--transcript", "build", "login");
        Assert.Equal("build login", goal);
    }

    [Fact]
    public void ResolveGoal_ShouldIgnoreRemoteAndTranscriptFlags()
    {
        var goal = InvokeResolveGoal("--remote", "build", "--transcript", "login");
        Assert.Equal("build login", goal);
    }

    private static string InvokeResolveGoal(params string[] args)
    {
        var method = typeof(LocalAgentRunner).GetMethod(
            "ResolveGoal",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        var value = method!.Invoke(null, new object[] { args });
        return Assert.IsType<string>(value);
    }
}
