using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Infrastructure.Services;

namespace Bendover.Tests;

public class AgentPromptServiceTests : IDisposable
{
    private readonly string _originalCurrentDirectory;
    private readonly DirectoryInfo _tempDirectory;

    public AgentPromptServiceTests()
    {
        _originalCurrentDirectory = Directory.GetCurrentDirectory();
        _tempDirectory = Directory.CreateTempSubdirectory("agent-prompt-service-tests-");
    }

    [Fact]
    public void LoadLeadPromptTemplate_LoadsFromConfiguredPracticesRoot()
    {
        var agentsRoot = Path.Combine(_tempDirectory.FullName, ".bendover", "agents");
        Directory.CreateDirectory(agentsRoot);
        File.WriteAllText(Path.Combine(agentsRoot, "lead.md"), "Lead prompt");

        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var accessor = new PromptOptRunContextAccessor
        {
            Current = new PromptOptRunContext("/out", Capture: true)
        };
        var sut = new AgentPromptService(accessor);

        var prompt = sut.LoadLeadPromptTemplate();

        Assert.Equal("Lead prompt", prompt);
    }

    [Fact]
    public void LoadLeadPromptTemplate_Throws_WhenMissing()
    {
        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var accessor = new PromptOptRunContextAccessor
        {
            Current = new PromptOptRunContext("/out", Capture: true)
        };
        var sut = new AgentPromptService(accessor);

        var ex = Assert.Throws<InvalidOperationException>(() => sut.LoadLeadPromptTemplate());

        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".bendover/agents/lead.md", ex.Message.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadEngineerPromptTemplate_Throws_WhenEmpty()
    {
        var agentsDirectory = Path.Combine(_tempDirectory.FullName, ".bendover", "agents");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, "engineer.md"), "   ");

        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var accessor = new PromptOptRunContextAccessor
        {
            Current = new PromptOptRunContext("/out", Capture: true)
        };
        var sut = new AgentPromptService(accessor);

        var ex = Assert.Throws<InvalidOperationException>(() => sut.LoadEngineerPromptTemplate());
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetWorkspaceToolsMarkdownPath_UsesRunContextBundleAgentsRoot()
    {
        var accessor = new PromptOptRunContextAccessor
        {
            Current = new PromptOptRunContext(
                OutDir: "/out",
                Capture: true,
                PracticesRootRelativePath: ".bendover/promptopt/bundles/bundle-1/practices")
        };
        var sut = new AgentPromptService(accessor);

        var path = sut.GetWorkspaceToolsMarkdownPath();

        Assert.Equal("/workspace/.bendover/promptopt/bundles/bundle-1/agents/tools.md", path);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCurrentDirectory);
        if (_tempDirectory.Exists)
        {
            _tempDirectory.Delete(recursive: true);
        }
    }
}
