using System.Text.Json;

namespace Bendover.SDK;

public enum SdkActionEventType
{
    Start,
    Success,
    Failure
}

public sealed record SdkActionError(
    string Type,
    string Message,
    string StackTrace);

public sealed record SdkActionEvent(
    SdkActionEventType EventType,
    string MethodName,
    DateTimeOffset StartedAtUtc,
    long ElapsedMs,
    string? PayloadJson,
    SdkActionError? Error);

public interface ISdkActionEventSink
{
    void OnEvent(SdkActionEvent sdkEvent);
}

public sealed class CompositeSdkActionEventSink : ISdkActionEventSink
{
    private readonly IReadOnlyList<ISdkActionEventSink> _sinks;

    public CompositeSdkActionEventSink(params ISdkActionEventSink[] sinks)
    {
        _sinks = sinks
            .Where(static sink => sink is not null)
            .ToArray();
    }

    public void OnEvent(SdkActionEvent sdkEvent)
    {
        foreach (var sink in _sinks)
        {
            sink.OnEvent(sdkEvent);
        }
    }
}

public sealed class ConsoleJsonSdkEventSink : ISdkActionEventSink
{
    public void OnEvent(SdkActionEvent sdkEvent)
    {
        var payload = new
        {
            event_type = ToEventTypeName(sdkEvent.EventType),
            method_name = sdkEvent.MethodName,
            started_at_utc = sdkEvent.StartedAtUtc,
            elapsed_ms = sdkEvent.ElapsedMs,
            payload_json = sdkEvent.PayloadJson,
            error = sdkEvent.Error
        };

        Console.Out.WriteLine(JsonSerializer.Serialize(payload));
    }

    private static string ToEventTypeName(SdkActionEventType eventType)
    {
        return eventType switch
        {
            SdkActionEventType.Start => "sdk_action_start",
            SdkActionEventType.Success => "sdk_action_success",
            SdkActionEventType.Failure => "sdk_action_failure",
            _ => "sdk_action_unknown"
        };
    }
}

public sealed class InMemorySdkEventSink : ISdkActionEventSink
{
    public SdkActionEvent? LastSuccessEvent { get; private set; }

    public SdkActionEvent? LastFailureEvent { get; private set; }

    public void OnEvent(SdkActionEvent sdkEvent)
    {
        if (sdkEvent.EventType == SdkActionEventType.Success)
        {
            LastSuccessEvent = sdkEvent;
            return;
        }

        if (sdkEvent.EventType == SdkActionEventType.Failure)
        {
            LastFailureEvent = sdkEvent;
        }
    }
}
