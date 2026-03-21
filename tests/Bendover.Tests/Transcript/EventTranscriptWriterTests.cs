using Bendover.Application;
using Bendover.Application.Transcript;
using Bendover.Domain.Interfaces;
using Microsoft.Extensions.AI;
using Moq;

namespace Bendover.Tests.Transcript;

public class EventTranscriptWriterTests
{
    [Fact]
    public async Task WritePromptAsync_ShouldPublishPromptAndAuditTranscriptEvents()
    {
        var events = new List<AgentTranscriptEvent>();
        var writer = new EventTranscriptWriter(CreateEventPublisher(events));

        await writer.WriteSelectedPracticesAsync(new[] { "tdd_spirit", "small_steps" });
        await writer.WritePromptAsync(
            "engineer_step_1",
            new List<ChatMessage>
            {
                new(ChatRole.System, """
                    Selected Practices:
                    - [tdd_spirit] Write tests first
                    """),
                new(ChatRole.User, "Build feature")
            });

        Assert.Contains(events, evt => evt.Category == "run" && evt.Message.Contains("selected_practices=", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Category == "prompt" && evt.Phase == "engineer_step_1" && evt.Message.Contains("system_selected_practices=tdd_spirit", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Category == "audit" && evt.Phase == "engineer_step_1" && evt.Message.Contains("practice=tdd_spirit delivered=yes", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Category == "audit" && evt.Phase == "engineer_step_1" && evt.Message.Contains("practice=small_steps delivered=no", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WriteOutputAndFailureAsync_ShouldPreserveTranscriptFormatting()
    {
        var events = new List<AgentTranscriptEvent>();
        var writer = new EventTranscriptWriter(CreateEventPublisher(events));

        await writer.WriteOutputAsync("engineer_step_1", new string('a', 360));
        await writer.WriteFailureAsync("engineer_step_failure_1", "failed_checks=script_exit_non_zero");

        Assert.Contains(events, evt => evt.Category == "output" && evt.Message.Contains("[transcript][output] phase=engineer_step_1", StringComparison.Ordinal) && evt.Message.Contains("...(truncated)", StringComparison.Ordinal));
        Assert.Contains(events, evt => evt.Category == "failure" && evt.Message.Contains("[transcript][failure] phase=engineer_step_failure_1", StringComparison.Ordinal) && evt.Message.Contains("failed_checks=script_exit_non_zero", StringComparison.Ordinal));
    }

    private static IAgentEventPublisher CreateEventPublisher(List<AgentTranscriptEvent> events)
    {
        var observer = new Mock<IAgentObserver>();
        observer
            .Setup(x => x.OnEventAsync(It.IsAny<AgentEvent>()))
            .Callback<AgentEvent>(evt =>
            {
                if (evt is AgentTranscriptEvent transcript)
                {
                    events.Add(transcript);
                }
            })
            .Returns(Task.CompletedTask);

        return new AgentEventPublisher(new[] { observer.Object });
    }
}
