using Bendover.Application.Evaluation;

namespace Bendover.Tests.Evaluation;

public class RulePracticeConventionMatcherTests
{
    [Fact]
    public void Matches_TddSpirit_To_TDDSpiritRule_CaseInsensitive()
    {
        var sut = new RulePracticeConventionMatcher();

        var matched = sut.Match("TDDSpiritRule", new[] { "tdd_spirit" });

        Assert.Equal(new[] { "tdd_spirit" }, matched);
    }

    [Fact]
    public void Matches_CleanInterfaces_To_CleanInterfacesRule()
    {
        var sut = new RulePracticeConventionMatcher();

        var matched = sut.Match("CleanInterfacesRule", new[] { "clean_interfaces" });

        Assert.Equal(new[] { "clean_interfaces" }, matched);
    }

    [Fact]
    public void Ignores_Separators_And_Casing()
    {
        var sut = new RulePracticeConventionMatcher();

        var matched = sut.Match("cleaninterfacesrule", new[] { "Clean-Interfaces" });

        Assert.Equal(new[] { "Clean-Interfaces" }, matched);
    }

    [Fact]
    public void Returns_Empty_When_No_Match()
    {
        var sut = new RulePracticeConventionMatcher();

        var matched = sut.Match("MissingTestsRule", new[] { "tdd_spirit", "clean_interfaces" });

        Assert.Empty(matched);
    }
}
