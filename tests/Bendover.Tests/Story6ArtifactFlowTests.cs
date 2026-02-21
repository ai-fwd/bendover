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
            var architectClientMock = new Mock<IChatClient>();
            var engineerClientMock = new Mock<IChatClient>();
            var reviewerClientMock = new Mock<IChatClient>();
            var containerServiceMock = new Mock<IContainerService>();
            var agenticTurnServiceMock = new Mock<IAgenticTurnService>();
            var environmentValidatorMock = new Mock<IEnvironmentValidator>();
            var observerMock = new Mock<IAgentObserver>();
            var gitRunnerMock = new Mock<IGitRunner>();

            environmentValidatorMock.Setup(x => x.ValidateAsync()).Returns(Task.CompletedTask);
            observerMock.Setup(x => x.OnEventAsync(It.IsAny<AgentEvent>())).Returns(Task.CompletedTask);
            leadAgentMock.Setup(x => x.AnalyzeTaskAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Practice>>()))
                .ReturnsAsync(new[] { "tdd_spirit" });

            architectClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "Plan content") }));
            engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.File.Write(\"a.txt\", \"artifact\");") }));
            reviewerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "ok") }));

            clientResolverMock.Setup(x => x.GetClient(AgentRole.Architect)).Returns(architectClientMock.Object);
            clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(engineerClientMock.Object);
            clientResolverMock.Setup(x => x.GetClient(AgentRole.Reviewer)).Returns(reviewerClientMock.Object);

            containerServiceMock.Setup(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()))
                .Returns(Task.CompletedTask);
            containerServiceMock.Setup(x => x.ResetWorkspaceAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new SandboxExecutionResult(0, "reset", string.Empty, "reset"));
            containerServiceMock.Setup(x => x.ApplyPatchAsync(It.IsAny<string>(), It.IsAny<bool>()))
                .ReturnsAsync(new SandboxExecutionResult(0, "apply", string.Empty, "apply"));
            agenticTurnServiceMock.SetupSequence(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
                .ReturnsAsync(new AgenticTurnObservation(
                    ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                    DiffExecution: new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt\n+artifact", string.Empty, "diff --git a/a.txt b/a.txt\n+artifact"),
                    ChangedFilesExecution: new SandboxExecutionResult(0, "a.txt", string.Empty, "a.txt"),
                    BuildExecution: new SandboxExecutionResult(-1, string.Empty, string.Empty, "skipped"),
                    ChangedFiles: new[] { "a.txt" },
                    HasChanges: true,
                    BuildPassed: false,
                    Action: new AgenticStepAction(AgenticStepActionKind.MutationWrite, "sdk.File.Write")))
                .ReturnsAsync(new AgenticTurnObservation(
                    ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                    DiffExecution: new SandboxExecutionResult(0, "diff --git a/a.txt b/a.txt\n+artifact", string.Empty, "diff --git a/a.txt b/a.txt\n+artifact"),
                    ChangedFilesExecution: new SandboxExecutionResult(0, "a.txt", string.Empty, "a.txt"),
                    BuildExecution: new SandboxExecutionResult(0, "sandbox build output", string.Empty, "sandbox build output"),
                    ChangedFiles: new[] { "a.txt" },
                    HasChanges: true,
                    BuildPassed: true,
                    Action: new AgenticStepAction(AgenticStepActionKind.Complete, "sdk.Signal.Done")));
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
            Assert.True(File.Exists(Path.Combine(outDir, "git_diff.patch")));
            Assert.True(File.Exists(Path.Combine(outDir, "dotnet_build.txt")));
            Assert.True(File.Exists(Path.Combine(outDir, "dotnet_test.txt")));
            Assert.Equal("diff --git a/a.txt b/a.txt\n+artifact", File.ReadAllText(Path.Combine(outDir, "git_diff.patch")));
            Assert.Equal("sandbox build output", File.ReadAllText(Path.Combine(outDir, "dotnet_build.txt")));
            Assert.Equal("sandbox test output", File.ReadAllText(Path.Combine(outDir, "dotnet_test.txt")));
            var runResultPath = Path.Combine(outDir, "run_result.json");
            Assert.True(File.Exists(runResultPath));
            using var runResultDoc = JsonDocument.Parse(File.ReadAllText(runResultPath));
            Assert.Equal("completed", runResultDoc.RootElement.GetProperty("status").GetString());
            Assert.True(runResultDoc.RootElement.GetProperty("has_code_changes").GetBoolean());
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
            leadAgentMock.Setup(x => x.AnalyzeTaskAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<Practice>>()))
                .ReturnsAsync(new[] { "tdd_spirit" });
            engineerClientMock.Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "sdk.Signal.Done();") }));
            clientResolverMock.Setup(x => x.GetClient(AgentRole.Engineer)).Returns(engineerClientMock.Object);

            containerServiceMock.Setup(x => x.StartContainerAsync(It.IsAny<SandboxExecutionSettings>()))
                .Returns(Task.CompletedTask);
            agenticTurnServiceMock.Setup(x => x.ExecuteAgenticTurnAsync(It.IsAny<string>(), It.IsAny<AgenticTurnSettings>()))
                .ReturnsAsync(new AgenticTurnObservation(
                    ScriptExecution: new SandboxExecutionResult(0, "ok", string.Empty, "ok"),
                    DiffExecution: new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty),
                    ChangedFilesExecution: new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty),
                    BuildExecution: new SandboxExecutionResult(1, "build failed", string.Empty, "build failed"),
                    ChangedFiles: Array.Empty<string>(),
                    HasChanges: false,
                    BuildPassed: false,
                    Action: new AgenticStepAction(AgenticStepActionKind.Complete, "sdk.Signal.Done")));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && git diff"))
                .ReturnsAsync(new SandboxExecutionResult(0, string.Empty, string.Empty, string.Empty));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet build Bendover.sln"))
                .ReturnsAsync(new SandboxExecutionResult(0, "sandbox build output", string.Empty, "sandbox build output"));
            containerServiceMock.Setup(x => x.ExecuteCommandAsync("cd /workspace && dotnet test"))
                .ReturnsAsync(new SandboxExecutionResult(0, "sandbox test output", string.Empty, "sandbox test output"));
            containerServiceMock.Setup(x => x.StopContainerAsync())
                .Returns(Task.CompletedTask);

            gitRunnerMock.Setup(x => x.RunAsync("rev-parse HEAD", It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync("abc123");
            agentPromptServiceMock.Setup(x => x.LoadEngineerPromptTemplate())
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
