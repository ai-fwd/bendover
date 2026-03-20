namespace Bendover.Application.Turn;

public sealed record RunRecordingOptions(
    bool RecordPrompt,
    bool RecordOutput,
    bool RecordArtifacts)
{
    public static RunRecordingOptions Default { get; } = new(
        RecordPrompt: true,
        RecordOutput: true,
        RecordArtifacts: true);
}
