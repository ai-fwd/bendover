using Bendover.Application.Interfaces;

namespace Bendover.Application;

public sealed class AgentPromptService : IAgentPromptService
{
    public const string ToolsContractHeading = "# SDK Tool Usage Contract (Auto-generated)";

    private const string DefaultAgentsRootRelativePath = ".bendover/agents";
    private const string LeadPromptFileName = "lead.md";
    private const string EngineerPromptFileName = "engineer.md";
    private const string ToolsMarkdownFileName = "tools.md";

    private readonly IPromptOptRunContextAccessor _runContextAccessor;

    public AgentPromptService(IPromptOptRunContextAccessor runContextAccessor)
    {
        _runContextAccessor = runContextAccessor;
    }

    public string LoadLeadPromptTemplate()
    {
        return LoadRequiredTemplate(LeadPromptFileName);
    }

    public string LoadEngineerPromptTemplate()
    {
        return LoadRequiredTemplate(EngineerPromptFileName);
    }

    public string GetWorkspaceAgentsDirectory()
    {
        return BuildWorkspacePath();
    }

    public string GetWorkspaceToolsMarkdownPath()
    {
        return BuildWorkspacePath(ToolsMarkdownFileName);
    }

    public string GetHostToolsMarkdownPath()
    {
        return BuildHostPath(ToolsMarkdownFileName);
    }

    public string GetAgentsRootRelativePath()
    {
        var configuredPracticesRoot = _runContextAccessor.Current?.PracticesRootRelativePath;
        if (string.IsNullOrWhiteSpace(configuredPracticesRoot))
        {
            return DefaultAgentsRootRelativePath;
        }

        var normalized = NormalizeRelativePath(configuredPracticesRoot).TrimEnd('/');
        if (normalized.EndsWith("/agents", StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        if (normalized.EndsWith("/practices", StringComparison.OrdinalIgnoreCase))
        {
            return $"{normalized[..^"/practices".Length]}/agents";
        }

        return $"{normalized}/agents";
    }

    private string LoadRequiredTemplate(string fileName)
    {
        var path = BuildHostPath(fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Required agent prompt file is missing: {path}");
        }

        var content = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"Required agent prompt file is empty: {path}");
        }

        return content;
    }

    private string BuildHostPath(params string[] segments)
    {
        var agentsRoot = ResolveHostAgentsRoot();
        var allSegments = new[] { agentsRoot }.Concat(segments).ToArray();
        return Path.Combine(allSegments);
    }

    private string BuildWorkspacePath(params string[] segments)
    {
        var relativeRoot = NormalizeRelativePath(GetAgentsRootRelativePath());
        if (Path.IsPathRooted(relativeRoot))
        {
            if (!relativeRoot.StartsWith("/workspace/", StringComparison.Ordinal)
                && !string.Equals(relativeRoot, "/workspace", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Agents root path must be relative or under /workspace, but was '{relativeRoot}'.");
            }

            var absolute = Path.Combine(new[] { relativeRoot }.Concat(segments).ToArray());
            return absolute.Replace('\\', '/');
        }

        var workspaceRoot = "/workspace";
        var combined = Path.Combine(new[] { workspaceRoot, relativeRoot }.Concat(segments).ToArray());
        return combined.Replace('\\', '/');
    }

    private string ResolveHostAgentsRoot()
    {
        var configured = GetAgentsRootRelativePath();
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }

        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').Trim();
    }
}
