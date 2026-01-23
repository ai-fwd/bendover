namespace Bendover.Domain;

public interface ILeadAgent
{
    Task<IEnumerable<string>> AnalyzeTaskAsync(string userPrompt);
}
