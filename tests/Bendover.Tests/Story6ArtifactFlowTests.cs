using Bendover.Application;
using Bendover.Application.Interfaces;
using Bendover.Domain;
using Bendover.Domain.Entities;
using Bendover.Domain.Interfaces;
using Bendover.Infrastructure.Services;
using Microsoft.Extensions.AI;
using Moq;
using System.Text.Json;
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
            var engineerClientMock = new Mock<IChatClient>();
            var containerServiceMock = new Mock<IContainerService>();
            var agenticTurnServiceMock = new Mock<IAgenticTurnService>();
            var environmentValidatorMock = new Mock<IEnvironmentValidator>();
            var observerMock = new Mock<IAgentObserver>();
            var gitRunnerMock = new Mock<IGitRunner>();

            environmentValidatorMock.Setup(x => x.ValidateAsync()).Returns(Task.CompletedTask);
            observerMock.Setup(x => x.OnEventAsync(It.IsAny<AgentEvent>())).Returns(Task.CompletedTask);
            leadAgentMock.Setup(x => x.AnalyzeTaskAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
                .ReturnsAsync(new[] { "tdd_spirit" });

            engineerClientMock.SetupSequence(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.WriteFile(\"a.txt\", \"artifact\");") }))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.Done();") }));

            clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(engineerClientMock.Object);

            containerServiceMock.Setup(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()))
                .Returns(Task.CompletedTask);
            agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
                .ReturnsAsync(new AgenticTurnObservation(
                    ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                    DiffExecution: new SandboxExecutionResult(-1, string.Empty, string.Empty, "skipped"),
                    HasChanges: false,
                    Action: new AgenticStepAction("write_file", IsDone: false, Command: "sdk.WriteFile")))
                .ReturnsAsync(new AgenticTurnObservation(
                    ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                    DiffExecution: new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt\n+artifact", string.Empty, "diff --git a/a.txt b/a.txt\n+artifact"),
                    HasChanges: true,
                    Action: new AgenticStepAction("done", IsDone: true, Command: "sdk.Done")));

            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
                .ReturnsAsync(new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt\n+artifact", string.Empty, "diff --git a/a.txt b/a.txt\n+artifact"));
            containerServiceMock.Setup(x => x.StopContainerAsync())
                .Returns(Task.CompletedTask);

            gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync("abc123");
            agentPromptServiceMock.Setup(x => x.LoadEngineerPromptTemplate(It.IsAny<string?>()))
                .Returns("Engineer prompt template\n\nGenerated tools content");

            var runContextAccessor = new PromptOptRunContextAccessor
            {
                Current = new PromptOptRunContext(outDir, Capture: true, RunId: "run-1", BundleId: "bundle-1", ApplySandboxPatchToSource: false)
            };

            var runRecorder = new PromptOptRunRecorder(
                new System.IO.Abstractions.FileSystem(),
                runContextAccessor);

            var orchestrator = new AgentOrchestrator(
                agentPromptServiceMock.Object,
                clientResolverMock.Object,
                containerServiceMock.Object,
                new ScriptGenerator(),
                agenticTurnServiceMock.Object,
                environmentValidatorMock.Object,
                new[] { observerMock.Object },
                leadAgentMock.Object,
                runRecorder,
                runContextAccessor,
                gitRunnerMock.Object);

            var practices = new List<Practice>
            {
                new("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
            };

            await orchestrator.RunAsync("Do work", practices);

            containerServiceMock.Verify(
                x => x.StartContainerAsync(It.Is<SandboxExecutionSettings>(settings =>
                    string.Equals(settings.BaseCommit, "abc123", StringComparison.Ordinal)
                    && string.Equals(settings.SourceRepositoryPath, Directory.GetCurrentDirectory(), StringComparison.Ordinal))),
                Times.Once);

            var gitDiffPath = Path.Combine(outDir, "git_diff.patch");
            Assert.True(File.Exists(gitDiffPath));
            Assert.Equal("diff --git a/a.txt b/a.txt\n+artifact", File.ReadAllText(gitDiffPath));

            Assert.False(File.Exists(Path.Combine(outDir, "dotnet_build.txt")));
            Assert.False(File.Exists(Path.Combine(outDir, "dotnet_test.txt")));

            var runResultPath = Path.Combine(outDir, "run_result.json");
            Assert.True(File.Exists(runResultPath));
            using var runResultDoc = JsonDocument.Parse(File.ReadAllText(runResultPath));
            Assert.Equal("completed", runResultDoc.RootElement.GetProperty("status").GetString());
            Assert.True(runResultDoc.RootElement.GetProperty("has_code_changes").GetBoolean());
            Assert.Equal("done", runResultDoc.RootElement.GetProperty("completion_action_name").GetString());
        }
        finally
        {
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunAsync_PersistsNoDiffOutcome_AsCompletedWithoutCodeChanges()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"story6-artifacts-nodiff-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);

        try
        {
            var leadAgentMock = new Mock<ILeadAgent>();
            var agentPromptServiceMock = new Mock<IAgentPromptService>();
            var clientResolverMock = new Mock<IChatClientResolver>();
            var engineerClientMock = new Mock<IChatClient>();
            var containerServiceMock = new Mock<IContainerService>();
            var agenticTurnServiceMock = new Mock<IAgenticTurnService>();
            var environmentValidatorMock = new Mock<IEnvironmentValidator>();
            var observerMock = new Mock<IAgentObserver>();
            var gitRunnerMock = new Mock<IGitRunner>();

            environmentValidatorMock.Setup(x => x.ValidateAsync()).Returns(Task.CompletedTask);
            observerMock.Setup(x => x.OnEventAsync(It.IsAny<AgentEvent>())).Returns(Task.CompletedTask);
            leadAgentMock.Setup(x => x.AnalyzeTaskAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Practice>>(), It.IsAny<string?>()))
                .ReturnsAsync(new[] { "tdd_spirit" });
            engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.Done();") }));
            clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(engineerClientMock.Object);

            containerServiceMock.Setup(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()))
                .Returns(Task.CompletedTask);
            agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
                .ReturnsAsync(new AgenticTurnObservation(
                    ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                    DiffExecution: new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty),
                    HasChanges: false,
                    Action: new AgenticStepAction("done", IsDone: true, Command: "sdk.Done")));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
                .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty));
            containerServiceMock.Setup(x => x.StopContainerAsync())
                .Returns(Task.CompletedTask);

            gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync("abc123");
            agentPromptServiceMock.Setup(x => x.LoadEngineerPromptTemplate(It.IsAny<string?>()))
                .Returns("Engineer prompt template\n\nGenerated tools content");

            var runContextAccessor = new PromptOptRunContextAccessor
            {
                Current = new PromptOptRunContext(outDir, Capture: true, RunId: "run-1", BundleId: "bundle-1", ApplySandboxPatchToSource: false)
            };

            var runRecorder = new PromptOptRunRecorder(
                new System.IO.Abstractions.FileSystem(),
                runContextAccessor);

            var orchestrator = new AgentOrchestrator(
                agentPromptServiceMock.Object,
                clientResolverMock.Object,
                containerServiceMock.Object,
                new ScriptGenerator(),
                agenticTurnServiceMock.Object,
                environmentValidatorMock.Object,
                new[] { observerMock.Object },
                leadAgentMock.Object,
                runRecorder,
                runContextAccessor,
                gitRunnerMock.Object);

            var practices = new List<Practice>
            {
                new("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests first.")
            };

            await orchestrator.RunAsync("Do work", practices);

            var gitDiffPath = Path.Combine(outDir, "git_diff.patch");
            Assert.True(File.Exists(gitDiffPath));
            Assert.Equal(string.Empty, File.ReadAllText(gitDiffPath));

            var runResultPath = Path.Combine(outDir, "run_result.json");
            Assert.True(File.Exists(runResultPath));
            using var runResultDoc = JsonDocument.Parse(File.ReadAllText(runResultPath));
            Assert.Equal("completed", runResultDoc.RootElement.GetProperty("status").GetString());
            Assert.False(runResultDoc.RootElement.GetProperty("has_code_changes").GetBoolean());
            Assert.Equal(0, runResultDoc.RootElement.GetProperty("git_diff_bytes").GetInt32());
            Assert.Equal("done", runResultDoc.RootElement.GetProperty("completion_action_name").GetString());
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
