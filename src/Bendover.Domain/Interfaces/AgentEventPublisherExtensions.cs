namespace Bendover.Domain.Interfaces;

public static class AgentEventPublisherExtensions
{
    public static Task ProgressAsync(this IAgentEventPublisher publisher, string message)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        return publisher.PublishAsync(new AgentProgressEvent(message));
    }

    public static Task StepAsync(this IAgentEventPublisher publisher, AgentStepEvent step)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        return publisher.PublishAsync(step);
    }

    public static Task TranscriptAsync(this IAgentEventPublisher publisher, AgentTranscriptEvent transcript)
    {
        ArgumentNullException.ThrowIfNull(publisher);
        return publisher.PublishAsync(transcript);
    }
}
