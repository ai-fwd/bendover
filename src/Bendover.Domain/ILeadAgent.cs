namespace Bendover.Domain;

public interface ILeadAgent
{
    Task<IEnumerable<string>> AnalyzeTaskAsync(string userPrompt, IReadOnlyCollection<Practice> practices, string? agentsPath = null);
}
