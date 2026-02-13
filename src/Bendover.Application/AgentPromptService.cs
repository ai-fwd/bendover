using Bendover.Application.Interfaces;
using Bendover.Domain.Interfaces;

namespace Bendover.Application;

public sealed class AgentPromptService : IAgentPromptService
{
    public const string ToolsContractHeading = "# SDK Tool Usage Contract (Auto-generated)";

    private const string DefaultAgentsRelativePath = ".bendover/agents";
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
        return LoadRequiredTemplate(EngineerPromptFileName, agentsPath);
    }

    public string GetWorkspaceToolsMarkdownPath(string? agentsPath = null)
    {
        return BuildWorkspacePath(ToolsMarkdownFileName, agentsPath);
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

    private string BuildWorkspacePath(string fileName, string? agentsPath)
    {
        var workspaceAgentsPath = ResolveWorkspaceAgentsPath(agentsPath);
        var fullPath = Path.Combine(workspaceAgentsPath, fileName);
        return fullPath.Replace('\\', '/');
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

    private string ResolveWorkspaceAgentsPath(string? agentsPath)
    {
        var configured = string.IsNullOrWhiteSpace(agentsPath)
            ? (_agentsPath ?? DefaultAgentsRelativePath)
            : agentsPath;
        var normalized = NormalizePath(configured).TrimEnd('/');

        if (Path.IsPathRooted(normalized))
        {
            if (!normalized.StartsWith("/workspace/", StringComparison.Ordinal)
                && !string.Equals(normalized, "/workspace", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Agents path for workspace must be relative or under /workspace, but was '{normalized}'.");
            }

            return normalized;
        }

        return Path.Combine("/workspace", normalized).Replace('\\', '/');
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }
}
