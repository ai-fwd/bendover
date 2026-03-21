using Mystro.Application.Interfaces;
using Mystro.Application.Run;
using Mystro.Application.Transcript;
using Mystro.Domain;
using Mystro.Domain.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Mystro.Tests.Run;

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
