using Bendover.Application.Turn;
using Bendover.Application.Interfaces;

namespace Bendover.Tests.Turn;

public class TurnBuilderTests
{
    [Fact]
    public async Task Build_ShouldUseNoOpTranscriptWriter_ByDefault()
    {
        FakeStep? capturedStep = null;
        var builder = new TurnBuilder(
            (type, capabilities) =>
            {
                capturedStep = new FakeStep(capabilities);
                return capturedStep;
            },
            _ => Task.CompletedTask);

        var pipeline = builder
            .Add<FakeStep>()
            .Build();

        await pipeline(CreateContext());

        Assert.NotNull(capturedStep);
        Assert.IsType<NoOpTranscriptWriter>(capturedStep!.TranscriptWriter);
    }

    [Fact]
    public async Task Build_ShouldUseStreamingTranscriptWriter_WhenEnabled()
    {
        var transcriptMessages = new List<string>();
        FakeStep? capturedStep = null;
        var builder = new TurnBuilder(
            (type, capabilities) =>
            {
                capturedStep = new FakeStep(capabilities);
                return capturedStep;
            },
            message =>
            {
                transcriptMessages.Add(message);
                return Task.CompletedTask;
            });

        var pipeline = builder
            .WithTranscript(enabled: true, selectedPractices: new[] { "practice" })
            .Add<FakeStep>()
            .Build();

        await pipeline(CreateContext());

        Assert.NotNull(capturedStep);
        Assert.IsType<StreamingTranscriptWriter>(capturedStep!.TranscriptWriter);
        Assert.Single(transcriptMessages);
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

    private sealed class FakeStep : TurnStep
    {
        public FakeStep(TurnCapabilities capabilities)
        {
            TranscriptWriter = capabilities.TranscriptWriter;
        }

        public ITranscriptWriter TranscriptWriter { get; }

        public override async Task InvokeAsync(TurnContext context, TurnDelegate next)
        {
            await TranscriptWriter.WriteOutputAsync("phase", "output");
            await next(context);
        }
    }
}
