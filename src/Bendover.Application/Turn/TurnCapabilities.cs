namespace Bendover.Application.Turn;

public sealed class TurnCapabilities
{
    public ITranscriptWriter TranscriptWriter { get; set; } = new NoOpTranscriptWriter();

    public RunRecordingOptions RunRecording { get; set; } = RunRecordingOptions.Default;
}
