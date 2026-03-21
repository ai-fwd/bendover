using Bendover.Application;
using Bendover.Application.Turn;
using Bendover.Application.Interfaces;
using Bendover.Application.Transcript;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Bendover.Tests.Turn;

public class TurnBuilderTests
{
    [Fact]
    public async Task Build_ShouldUseNoOpTranscriptWriter_ByDefault()
    {
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

        Assert.IsType<NoOpTranscriptWriter>(run.TranscriptWriter);
    }

    [Fact]
    public async Task Build_ShouldUseEventTranscriptWriter_WhenEnabled()
    {
        var transcriptEvents = new List<AgentTranscriptEvent>();
        var recorder = CreateRecorderMock();
        var engineerClient = CreateEngineerClientMock();
        var run = CreateRunContext(
            new EventTranscriptWriter(CreateEventPublisher(evt =>
            {
                if (evt is AgentTranscriptEvent transcript)
                {
                    transcriptEvents.Add(transcript);
                }
            })),
            recorder.Object,
            engineerClient.Object);
        var builder = TurnBuilder.Create(run);

        var pipeline = builder
            .Add<InvokeAgentStep>()
            .Build();

        await pipeline(CreateContext(run));

        Assert.NotEmpty(transcriptEvents);
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
            Events = CreateEventPublisher(_ => { }),
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

    private static IAgentEventPublisher CreateEventPublisher(Action<AgentEvent> capture)
    {
        var observer = new Mock<IAgentObserver>();
        observer
            .Setup(x => x.OnEventAsync(It.IsAny<AgentEvent>()))
            .Callback<AgentEvent>(capture)
            .Returns(Task.CompletedTask);
        return new AgentEventPublisher(new[] { observer.Object });
    }
}
