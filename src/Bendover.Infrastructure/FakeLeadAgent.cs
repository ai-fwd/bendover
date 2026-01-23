using Bendover.Domain;

namespace Bendover.Infrastructure;

public class FakeLeadAgent : ILeadAgent
{
    public Task<IEnumerable<string>> AnalyzeTaskAsync(string userPrompt)
    {
        // Hardcoded as per requirements
        return Task.FromResult<IEnumerable<string>>(new[] { "tdd_spirit", "clean_interfaces" });
    }
}
