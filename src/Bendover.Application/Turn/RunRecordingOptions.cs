namespace Bendover.Application.Turn;

public sealed record RunRecordingOptions(
    bool RecordPrompt,
    bool RecordOutput)
{
    public static RunRecordingOptions Default { get; } = new(
        RecordPrompt: true,
        RecordOutput: true);
}
