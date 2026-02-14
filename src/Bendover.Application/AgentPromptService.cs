using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;

namespace Bendover.Application;

public sealed class AgentPromptService : IAgentPromptService
{
    private const string LeadPromptFileName = "lead.md";
    private const string EngineerPromptFileName = "engineer.md";
    private const string ToolsMarkdownFileName = "tools.md";

    private readonly IFileService _fileService;
    private readonly string? _agentsPath;

    public AgentPromptService(IFileService fileService, string? agentsPath = null)
    {
        _fileService = fileService;
        _agentsPath = agentsPath;
    }

    public string LoadLeadPromptTemplate(string? agentsPath = null)
    {
        return LoadRequiredTemplate(LeadPromptFileName, agentsPath);
    }

    public string LoadEngineerPromptTemplate(string? agentsPath = null)
    {
        var engineerTemplate = LoadRequiredTemplate(EngineerPromptFileName, agentsPath);
        var toolsTemplate = LoadRequiredTemplate(ToolsMarkdownFileName, agentsPath);
        return $"{engineerTemplate}\n\n{toolsTemplate}";
    }

    private string LoadRequiredTemplate(string fileName, string? agentsPath)
    {
        var path = BuildHostPath(fileName, agentsPath);
        if (!_fileService.Exists(path))
        {
            throw new InvalidOperationException($"Required agent prompt file is missing: {path}");
        }

        var content = _fileService.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"Required agent prompt file is empty: {path}");
        }

        return content;
    }

    private string BuildHostPath(string fileName, string? agentsPath)
    {
        var hostAgentsPath = ResolveHostAgentsPath(agentsPath);
        return Path.Combine(hostAgentsPath, fileName);
    }

    private string ResolveHostAgentsPath(string? agentsPath)
    {
        if (!string.IsNullOrWhiteSpace(agentsPath))
        {
            return ResolveHostPath(agentsPath);
        }

        if (!string.IsNullOrWhiteSpace(_agentsPath))
        {
            return ResolveHostPath(_agentsPath);
        }

        return Path.Combine(BendoverPaths.GetApplicationRoot(), ".bendover", "agents");
    }

    private static string ResolveHostPath(string path)
    {
        var configured = NormalizePath(path);
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }
}
