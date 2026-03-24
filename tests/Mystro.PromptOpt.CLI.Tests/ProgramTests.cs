using System.Reflection;

namespace Mystro.PromptOpt.CLI.Tests;

public class ProgramTests
{
    [Fact]
    public void BuildErrorMessage_ShouldIncludeInnerExceptionDetails()
    {
        var inner = new InvalidOperationException("payload too large");
        var outer = new Exception("top-level failure", inner);
        var method = GetPrivateStaticMethod("BuildErrorMessage");

        Assert.NotNull(method);
        var message = Assert.IsType<string>(method!.Invoke(null, new object[] { outer }));

        Assert.Contains("Exception: top-level failure", message, StringComparison.Ordinal);
        Assert.Contains("Inner[1] InvalidOperationException: payload too large", message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadReplayGoal_ShouldThrow_WhenGoalFileMissing()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var method = GetPrivateStaticMethod("LoadReplayGoal");
            var ex = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, new object[] { tempDir }));
            Assert.IsType<FileNotFoundException>(ex.InnerException);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadScoreGoal_ShouldThrow_WhenGoalFileEmpty()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "goal.txt"), "   ");

            var method = GetPrivateStaticMethod("LoadScoreGoal");
            var ex = Assert.Throws<TargetInvocationException>(() => method!.Invoke(null, new object[] { tempDir }));
            Assert.IsType<InvalidOperationException>(ex.InnerException);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadScoreGoal_ShouldIncludeGoalPrefix_WhenGoalExists()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "goal.txt"), "Ship feature X");

            var method = GetPrivateStaticMethod("LoadScoreGoal");
            var result = Assert.IsType<string>(method!.Invoke(null, new object[] { tempDir }));
            Assert.Equal("Score existing run: Ship feature X", result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static MethodInfo? GetPrivateStaticMethod(string methodName)
    {
        return typeof(RunCommand).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mystro_promptopt_cli_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
