using Bendover.Application.Turn;
using Bendover.Application.Interfaces;
using Microsoft.Extensions.AI;
using Moq;

namespace Bendover.Tests.Turn;

public class TurnBuilderTests
{
    [Fact]
    public async Task Build_ShouldUseNoOpTranscriptWriter_ByDefault()
    {
        var transcriptMessages = new List<string>();
        var recorder = CreateRecorderMock();
        var engineerClient = CreateEngineerClientMock();

        var builder = TurnBuilder.Create(
            message =>
            {
                transcriptMessages.Add(message);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            engineerClient.Object,
            new Mock<IAgenticTurnService>().Object,
            recorder.Object,
            engineerPromptTemplate: "template");

        var pipeline = builder
            .Add<InvokeAgentStep>()
            .Build();

        await pipeline(CreateContext());

        Assert.Empty(transcriptMessages);
    }

    [Fact]
    public async Task Build_ShouldUseStreamingTranscriptWriter_WhenEnabled()
    {
        var transcriptMessages = new List<string>();
        var recorder = CreateRecorderMock();
        var engineerClient = CreateEngineerClientMock();
        var builder = TurnBuilder.Create(
            message =>
            {
                transcriptMessages.Add(message);
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            engineerClient.Object,
            new Mock<IAgenticTurnService>().Object,
            recorder.Object,
            engineerPromptTemplate: "template");

        var pipeline = builder
            .WithTranscript(enabled: true, selectedPractices: new[] { "practice" })
            .Add<InvokeAgentStep>()
            .Build();

        await pipeline(CreateContext());

        Assert.NotEmpty(transcriptMessages);
    }

    private static TurnContext CreateContext()
    {
        return new TurnContext
        {
            StepNumber = 1,
            Run = new TurnRunContext
            {
                StepHistory = new List<TurnHistoryEntry>(),
                TurnSettings = new Domain.Entities.AgenticTurnSettings()
            },
            PracticesContext = "practice",
            Plan = "plan",
            SelectedPractices = new[] { "practice" }
        };
    }

    private static Mock<IPromptOptRunRecorder> CreateRecorderMock()
    {
        var recorder = new Mock<IPromptOptRunRecorder>();
        recorder
            .Setup(x => x.RecordPromptAsync(It.IsAny<string>(), It.IsAny<List<ChatMessage>>()))
            .Returns(Task.CompletedTask);
        recorder
            .Setup(x => x.RecordOutputAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        return recorder;
    }

    private static Mock<IChatClient> CreateEngineerClientMock()
    {
        var client = new Mock<IChatClient>();
        client
            .Setup(x => x.CompleteAsync(It.IsAny<IList<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ChatCompletion(new[] { new ChatMessage(ChatRole.Assistant, "ok") }));

        return client;
    }
}
