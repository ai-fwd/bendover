using Bendover.Application.Interfaces;
using Bendover.Application.Turn;
using Bendover.Domain;

namespace Bendover.Application.Run;

public sealed class RunStageContext
{
    public required string InitialGoal { get; init; }
    public required IReadOnlyCollection<Practice> Practices { get; init; }
    public required string? AgentsPath { get; init; }
    public required PromptOptRunContext PromptOptRunContext { get; init; }
    public required string BundleId { get; init; }
    public required string SourceRepositoryPath { get; init; }
    public required Func<string, Task> NotifyProgressAsync { get; init; }

    public bool StreamTranscriptEnabled { get; set; }
    public string BaseCommit { get; set; } = string.Empty;
    public IReadOnlyCollection<string> SelectedPracticeNames { get; set; } = Array.Empty<string>();
    public IReadOnlyCollection<Practice> SelectedPractices { get; set; } = Array.Empty<Practice>();
    public string PracticesContext { get; set; } = string.Empty;
    public ITranscriptWriter TranscriptWriter { get; set; } = new NoOpTranscriptWriter();
    public string GitDiffContent { get; set; } = string.Empty;
    public RunResult? RunResult { get; set; }
    public Exception? TerminalException { get; set; }
}
