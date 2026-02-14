namespace Bendover.Application.Interfaces;

public interface IAgentPromptService
{
    string LoadLeadPromptTemplate(string? agentsPath = null);
    string LoadEngineerPromptTemplate(string? agentsPath = null);
}
