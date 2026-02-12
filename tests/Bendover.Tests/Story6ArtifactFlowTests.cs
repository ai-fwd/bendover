using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure.Services;
using Microsoft.Extensions.AI;
using Moq;
using Xunit;

namespace Bendover.Tests;

public class Story6ArtifactFlowTests
{
    [Fact]
    public async Task RunAsync_PersistsSandboxArtifactsToHostOutDir_WithExpectedNames()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"story6-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);

        try
        {
            var leadAgentMock = new Mock<ILeadAgent>();
            var agentPromptServiceMock = new Mock<IAgentPromptService>();
            var clientResolverMock = new Mock<IChatClientResolver>();
            var architectClientMock = new Mock<IChatClient>();
            var engineerClientMock = new Mock<IChatClient>();
            var reviewerClientMock = new Mock<IChatClient>();
            var containerServiceMock = new Mock<IContainerService>();
            var environmentValidatorMock = new Mock<IEnvironmentValidator>();
            var observerMock = new Mock<IAgentObserver>();
            var gitRunnerMock = new Mock<IGitRunner>();

            environmentValidatorMock.Setup(x => x.ValidateAsync()).Returns(Task.CompletedTask);
            observerMock.Setup(x => x.OnProgressAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
            leadAgentMock.Setup(x => x.AnalyzeTaskAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Practice>>()))
                .ReturnsAsync(new[] { "tdd_spirit" });

            architectClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Plan content") }));
            engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Console.WriteLine(\"artifact run\");") }));
            reviewerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "ok") }));

            clientResolverMock.Setup(x => x.GetClient(AgentRole.Architect)).Returns(architectClientMock.Object);
            clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(engineerClientMock.Object);
            clientResolverMock.Setup(x => x.GetClient(AgentRole.Reviewer)).Returns(reviewerClientMock.Object);

            containerServiceMock.Setup(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()))
                .Returns(Task.CompletedTask);
            containerServiceMock.Setup(x => x.ExecuteEngineerBodyAsync(It.IsAny<string>()))
                .ReturnsAsync(new SandboxExecutionResult(0, "ok", string.Empty, "ok"));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cat '/workspace/.bendover/agents/tools.md'"))
                .ReturnsAsync(new SandboxExecutionResult(0, "# SDK Tool Usage Contract (Auto-generated)\n- sdk contract", string.Empty, "# SDK Tool Usage Contract (Auto-generated)\n- sdk contract"));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
                .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt\n+artifact", string.Empty, "diff --git a/a.txt b/a.txt\n+artifact"));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet build Bendover.sln"))
                .ReturnsAsync(new SandboxExecutionResult(0, "sandbox build output", string.Empty, "sandbox build output"));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet test"))
                .ReturnsAsync(new SandboxExecutionResult(0, "sandbox test output", string.Empty, "sandbox test output"));
            containerServiceMock.Setup(x => x.StopContainerAsync())
                .Returns(Task.CompletedTask);

            gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync("abc123");
            agentPromptServiceMock.Setup(x => x.LoadEngineerPromptTemplate())
                .Returns("Engineer prompt template");
            agentPromptServiceMock.Setup(x => x.GetWorkspaceToolsMarkdownPath())
                .Returns("/workspace/.bendover/agents/tools.md");

            var runContextAccessor = new PromptOptRunContextAccessor
            {
                Current = new PromptOptRunContext(outDir, Capture: true, RunId: "run-1", BundleId: "bundle-1")
            };

            var runRecorder = new PromptOptRunRecorder(
                new System.IO.Abstractions.FileSystem(),
                runContextAccessor);

            var orchestrator = new AgentOrchestrator(
                agentPromptServiceMock.Object,
                clientResolverMock.Object,
                containerServiceMock.Object,
                new ScriptGenerator(),
                environmentValidatorMock.Object,
                new[] { observerMock.Object },
                leadAgentMock.Object,
                runRecorder,
                runContextAccessor,
                gitRunnerMock.Object,
                new EngineerBodyValidator());

            var practices = new List<Practice>
            {
                new("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
            };

            await orchestrator.RunAsync("Do work", practices);

            Assert.True(File.Exists(Path.Combine(outDir, "git_diff.patch")));
            Assert.True(File.Exists(Path.Combine(outDir, "dotnet_build.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "dotnet_test.txt")));
            Assert.Equal("diff --git a/a.txt b/a.txt\n+artifact", File.ReadAllText(Path.Combine(outDir, "git_diff.patch")));
            Assert.Equal("sandbox build output", File.ReadAllText(Path.Combine(outDir, "dotnet_build.txt")));
            Assert.Equal("sandbox test output", File.ReadAllText(Path.Combine(outDir, "dotnet_test.txt")));
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }
}
