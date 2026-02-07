using Bendover.Application.Evaluation;

namespace Bendover.Tests.Evaluation;

public class PracticeAttributionBuilderTests
{
    [Fact]
    public void FailedRule_WithConventionMatch_AddsOffendingPractice()
    {
        var matcher = new RulePracticeConventionMatcher();
        var sut = new PracticeAttributionBuilder(matcher);
        var selected = new[] { "tdd_spirit", "clean_interfaces" };
        var results = new[]
        {
            new RuleResult("TDDSpiritRule", false, 0f, new[] { "Needs stronger TDD." })
        };

        var attribution = sut.Build(results, selected);

        Assert.Equal(selected, attribution.SelectedPractices);
        Assert.Equal(new[] { "tdd_spirit" }, attribution.OffendingPractices);
    }

    [Fact]
    public void FailedRule_WithConventionMatch_AddsNotesByPractice_WhenRuleHasGlobalNotes()
    {
        var matcher = new RulePracticeConventionMatcher();
        var sut = new PracticeAttributionBuilder(matcher);
        var results = new[]
        {
            new RuleResult("TDDSpiritRule", false, 0f, new[] { "Write tests first." })
        };

        var attribution = sut.Build(results, new[] { "tdd_spirit" });

        Assert.True(attribution.NotesByPractice.ContainsKey("tdd_spirit"));
        Assert.Contains("Write tests first.", attribution.NotesByPractice["tdd_spirit"]);
    }

    [Fact]
    public void FailedRule_WithExplicitAffectedPractices_OverridesConvention()
    {
        var matcher = new RulePracticeConventionMatcher();
        var sut = new PracticeAttributionBuilder(matcher);
        var results = new[]
        {
            new RuleResult(
                "TDDSpiritRule",
                false,
                0f,
                new[] { "Only this practice should be flagged." },
                AffectedPractices: new[] { "clean_interfaces" })
        };

        var attribution = sut.Build(results, new[] { "tdd_spirit", "clean_interfaces" });

        Assert.Equal(new[] { "clean_interfaces" }, attribution.OffendingPractices);
    }

    [Fact]
    public void FailedRule_NoMatch_DoesNotAddPracticeAttribution()
    {
        var matcher = new RulePracticeConventionMatcher();
        var sut = new PracticeAttributionBuilder(matcher);
        var results = new[]
        {
            new RuleResult("MissingTestsRule", false, 0f, new[] { "No tests changed." })
        };

        var attribution = sut.Build(results, new[] { "tdd_spirit" });

        Assert.Empty(attribution.OffendingPractices);
        Assert.Empty(attribution.NotesByPractice);
    }

    [Fact]
    public void SelectedPractices_ArePreserved_InAttribution()
    {
        var matcher = new RulePracticeConventionMatcher();
        var sut = new PracticeAttributionBuilder(matcher);
        var selected = new[] { "tdd_spirit", "clean_interfaces" };

        var attribution = sut.Build(Array.Empty<RuleResult>(), selected);

        Assert.Equal(selected, attribution.SelectedPractices);
    }
}
