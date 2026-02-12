namespace Bendover.Application.Interfaces;

public interface IAgentPromptService
{
    string LoadLeadPromptTemplate();
    string LoadEngineerPromptTemplate();
    string GetWorkspaceAgentsDirectory();
    string GetWorkspaceToolsMarkdownPath();
    string GetHostToolsMarkdownPath();
    string GetAgentsRootRelativePath();
}
