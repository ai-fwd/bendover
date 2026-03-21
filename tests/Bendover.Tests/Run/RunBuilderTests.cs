using Bendover.Application.Interfaces;
using Bendover.Application.Run;
using Bendover.Application.Transcript;
using Bendover.Domain;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Bendover.Tests.Run;

public class RunBuilderTests
{
    [Fact]
    public async Task Build_ShouldUseEventTranscriptWriter_WhenTranscriptStreamingEnabled()
    {
        var context = CreateContext();
        ITranscriptWriter? capturedWriter = null;

        var pipeline = RunBuilder.Create(CreateRunStageFactory())
            .ConfigureTranscript(true)
            .Build();

        await pipeline(context, ctx =>
        {
            capturedWriter = ctx.TranscriptWriter;
            return Task.CompletedTask;
        });

        Assert.IsType<EventTranscriptWriter>(capturedWriter);
    }

    [Fact]
    public async Task Build_ShouldUseNoOpTranscriptWriter_WhenTranscriptStreamingDisabled()
    {
        var context = CreateContext();
        ITranscriptWriter? capturedWriter = null;

        var pipeline = RunBuilder.Create(CreateRunStageFactory())
            .ConfigureTranscript(false)
            .Build();

        await pipeline(context, ctx =>
        {
            capturedWriter = ctx.TranscriptWriter;
            return Task.CompletedTask;
        });

        Assert.IsType<NoOpTranscriptWriter>(capturedWriter);
    }

    private static RunStageContext CreateContext()
    {
        return new RunStageContext
        {
            InitialGoal = "goal",
            Practices = Array.Empty<Practice>(),
            AgentsPath = null,
            PromptOptRunContext = new PromptOptRunContext("/tmp/run", Capture: false, BundleId: "bundle"),
            BundleId = "bundle",
            SourceRepositoryPath = "/workspace",
            Events = new Mock<IAgentEventPublisher>().Object
        };
    }

    private static RunStageFactory CreateRunStageFactory()
    {
        return new RunStageFactory(new ServiceCollection().BuildServiceProvider());
    }
}
