using Bendover.Application.Turn;
using Bendover.Application.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
        var run = CreateRunContext(
            new NoOpTranscriptWriter(),
            recorder.Object,
            engineerClient.Object);

        var builder = TurnBuilder.Create(run);

        var pipeline = builder
            .Add<InvokeAgentStep>()
            .Build();

        await pipeline(CreateContext(run));

        Assert.Empty(transcriptMessages);
    }

    [Fact]
    public async Task Build_ShouldUseStreamingTranscriptWriter_WhenEnabled()
    {
        var transcriptMessages = new List<string>();
        var recorder = CreateRecorderMock();
        var engineerClient = CreateEngineerClientMock();
        var run = CreateRunContext(
            new StreamingTranscriptWriter(
                message =>
                {
                    transcriptMessages.Add(message);
                    return Task.CompletedTask;
                },
                new[] { "practice" }),
            recorder.Object,
            engineerClient.Object);
        var builder = TurnBuilder.Create(run);

        var pipeline = builder
            .Add<InvokeAgentStep>()
            .Build();

        await pipeline(CreateContext(run));

        Assert.NotEmpty(transcriptMessages);
    }

    private static RunContext CreateRunContext(
        ITranscriptWriter transcriptWriter,
        IPromptOptRunRecorder recorder,
        IChatClient engineerClient)
    {
        return new RunContext
        {
            StepFactory = CreateStepFactory(),
            TranscriptWriter = transcriptWriter,
            RunRecorder = recorder,
            EngineerClient = engineerClient,
            AgenticTurnService = new Mock<IAgenticTurnService>().Object,
            NotifyStepAsync = _ => Task.CompletedTask,
            EngineerPromptTemplate = "template",
            SelectedPractices = new[] { "practice" }
        };
    }

    private static TurnContext CreateContext(RunContext run)
    {
        return new TurnContext
        {
            StepNumber = 1,
            RunState = new TurnRunState
            {
                StepHistory = new List<TurnHistoryEntry>()
            },
            PracticesContext = "practice",
            Plan = "plan"
        };
    }

    private static TurnStepFactory CreateStepFactory()
    {
        var services = new ServiceCollection();
        services.AddTransient<InvokeAgentStep>();
        return new TurnStepFactory(services.BuildServiceProvider());
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
