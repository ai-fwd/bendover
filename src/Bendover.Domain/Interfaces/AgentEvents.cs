namespace Bendover.Domain.Interfaces;

public abstract record AgentEvent;

public sealed record AgentProgressEvent(string Message) : AgentEvent;

public sealed record AgentStepEvent(
    int StepNumber,
    string? Plan,
    string Tool,
    string Observation,
    bool IsCompletion) : AgentEvent;

public sealed record AgentTranscriptEvent(
    string Category,
    string Phase,
    string Message) : AgentEvent;
