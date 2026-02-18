using Bendover.Domain.Agentic;

namespace Bendover.Tests;

public class ShellCommandPolicyTests
{
    [Theory]
    [InlineData("rg")]
    [InlineData("grep")]
    [InlineData("find")]
    [InlineData("which")]
    [InlineData("head")]
    [InlineData("tail")]
    [InlineData("wc")]
    [InlineData("sort")]
    public void TryValidateAllowedForEngineer_AllowsExactReadOnlyCommandTokens(string command)
    {
        var allowed = ShellCommandPolicy.TryValidateAllowedForEngineer(command, out var violationReason);

        Assert.True(allowed);
        Assert.True(string.IsNullOrWhiteSpace(violationReason));
    }

    [Fact]
    public void TryValidateAllowedForEngineer_StillRejectsMutatingCommands()
    {
        var allowed = ShellCommandPolicy.TryValidateAllowedForEngineer("rm -f a.txt", out var violationReason);

        Assert.False(allowed);
        Assert.Contains("mutating and not allowed", violationReason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidateAllowedForEngineer_AllowsRgPatternWithQuotedPipeCharacters()
    {
        var command = "rg -n \"git author|user.name|user.email|bend@over.ai|sandbox\" -S src README.md docs issues.md setup.sh";

        var allowed = ShellCommandPolicy.TryValidateAllowedForEngineer(command, out var violationReason);

        Assert.True(allowed);
        Assert.True(string.IsNullOrWhiteSpace(violationReason));
    }

    [Fact]
    public void TryValidateAllowedForEngineer_AllowsFindPipelineToSort()
    {
        var command = "find . -type f | sort";

        var allowed = ShellCommandPolicy.TryValidateAllowedForEngineer(command, out var violationReason);

        Assert.True(allowed);
        Assert.True(string.IsNullOrWhiteSpace(violationReason));
    }

    [Fact]
    public void TryValidateAllowedForEngineer_RejectsUnsafePipelines()
    {
        var command = "cat README.md | bash";

        var allowed = ShellCommandPolicy.TryValidateAllowedForEngineer(command, out var violationReason);

        Assert.False(allowed);
        Assert.Contains("allowlist", violationReason, StringComparison.OrdinalIgnoreCase);
    }
}
