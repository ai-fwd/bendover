using Bendover.Application.Turn;
using Microsoft.Extensions.AI;

namespace Bendover.Tests.Turn;

public class TurnBuilderTests
{
    [Fact]
    public async Task Build_ShouldUseNoOpTranscriptWriter_ByDefault()
    {
        FakeStep? capturedStep = null;
        var builder = new TurnBuilder((type, capabilities) =>
        {
            capturedStep = new FakeStep(capabilities);
            return capturedStep;
        });

        var pipeline = builder
            .Add<FakeStep>()
            .Build();

        await pipeline(CreateContext());

        Assert.NotNull(capturedStep);
        Assert.IsType<NoOpTranscriptWriter>(capturedStep!.TranscriptWriter);
    }

    [Fact]
    public async Task Build_ShouldUseConfiguredTranscriptWriter_WhenEnabled()
    {
        var transcriptWriter = new FakeTranscriptWriter();
        FakeStep? capturedStep = null;
        var builder = new TurnBuilder((type, capabilities) =>
        {
            capturedStep = new FakeStep(capabilities);
            return capturedStep;
        });

        var pipeline = builder
            .WithTranscript(transcriptWriter)
            .Add<FakeStep>()
            .Build();

        await pipeline(CreateContext());

        Assert.NotNull(capturedStep);
        Assert.Same(transcriptWriter, capturedStep!.TranscriptWriter);
        Assert.Single(transcriptWriter.Outputs);
    }

    private static TurnContext CreateContext()
    {
        return new TurnContext
        {
            StepNumber = 1,
            EngineerPromptTemplate = "template",
            PracticesContext = "practice",
            Plan = "plan",
            SelectedPracticeNames = new[] { "practice" },
            StepHistory = new List<TurnHistoryEntry>(),
            TurnSettings = new Domain.Entities.AgenticTurnSettings()
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

    private sealed class FakeTranscriptWriter : ITranscriptWriter
    {
        public List<string> Outputs { get; } = new();

        public Task WritePromptAsync(string phase, IReadOnlyList<ChatMessage> messages, IReadOnlyCollection<string> selectedPractices)
            => Task.CompletedTask;

        public Task WriteOutputAsync(string phase, string output)
        {
            Outputs.Add($"{phase}:{output}");
            return Task.CompletedTask;
        }

        public Task WriteFailureAsync(string phase, string failureDigest)
            => Task.CompletedTask;
    }
}
