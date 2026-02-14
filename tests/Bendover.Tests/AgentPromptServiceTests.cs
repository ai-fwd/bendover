using Bendover.Application;
using Bendover.Infrastructure;

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
    public void LoadLeadPromptTemplate_LoadsFromBundleRootAgentsDirectory()
    {
        var agentsRoot = Path.Combine(_tempDirectory.FullName, ".bendover", "agents");
        Directory.CreateDirectory(agentsRoot);
        File.WriteAllText(Path.Combine(agentsRoot, "lead.md"), "Lead prompt");

        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var sut = new AgentPromptService(new FileService(new System.IO.Abstractions.FileSystem()));

        var prompt = sut.LoadLeadPromptTemplate();

        Assert.Equal("Lead prompt", prompt);
    }

    [Fact]
    public void LoadLeadPromptTemplate_Throws_WhenMissing()
    {
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, ".bendover"));
        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var sut = new AgentPromptService(new FileService(new System.IO.Abstractions.FileSystem()));

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
        var sut = new AgentPromptService(new FileService(new System.IO.Abstractions.FileSystem()));

        var ex = Assert.Throws<InvalidOperationException>(() => sut.LoadEngineerPromptTemplate());
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadEngineerPromptTemplate_ConcatenatesEngineerAndTools()
    {
        var agentsDirectory = Path.Combine(_tempDirectory.FullName, ".bendover", "agents");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, "engineer.md"), "Engineer prompt");
        File.WriteAllText(Path.Combine(agentsDirectory, "tools.md"), "Generated tools content");

        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var sut = new AgentPromptService(new FileService(new System.IO.Abstractions.FileSystem()));

        var prompt = sut.LoadEngineerPromptTemplate();

        Assert.Equal("Engineer prompt\n\nGenerated tools content", prompt);
    }

    [Fact]
    public void LoadEngineerPromptTemplate_UsesProvidedAgentsPath()
    {
        var agentsDirectory = Path.Combine(_tempDirectory.FullName, ".bendover", "promptopt", "bundles", "bundle-1", "agents");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, "engineer.md"), "Engineer from bundle");
        File.WriteAllText(Path.Combine(agentsDirectory, "tools.md"), "Tools from bundle");

        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var sut = new AgentPromptService(new FileService(new System.IO.Abstractions.FileSystem()));

        var prompt = sut.LoadEngineerPromptTemplate(".bendover/promptopt/bundles/bundle-1/agents");

        Assert.Equal("Engineer from bundle\n\nTools from bundle", prompt);
    }

    [Fact]
    public void LoadEngineerPromptTemplate_Throws_WhenToolsMissing()
    {
        var agentsDirectory = Path.Combine(_tempDirectory.FullName, ".bendover", "agents");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, "engineer.md"), "Engineer prompt");

        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var sut = new AgentPromptService(new FileService(new System.IO.Abstractions.FileSystem()));

        var ex = Assert.Throws<InvalidOperationException>(() => sut.LoadEngineerPromptTemplate());
        Assert.Contains("missing", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".bendover/agents/tools.md", ex.Message.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadEngineerPromptTemplate_Throws_WhenToolsEmpty()
    {
        var agentsDirectory = Path.Combine(_tempDirectory.FullName, ".bendover", "agents");
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(Path.Combine(agentsDirectory, "engineer.md"), "Engineer prompt");
        File.WriteAllText(Path.Combine(agentsDirectory, "tools.md"), " ");

        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        var sut = new AgentPromptService(new FileService(new System.IO.Abstractions.FileSystem()));

        var ex = Assert.Throws<InvalidOperationException>(() => sut.LoadEngineerPromptTemplate());
        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".bendover/agents/tools.md", ex.Message.Replace('\\', '/'), StringComparison.Ordinal);
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
