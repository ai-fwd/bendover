using Mystro.Application.Interfaces;
using Mystro.Application.Transcript;
using Mystro.Domain;
using Mystro.Domain.Interfaces;

namespace Mystro.Application.Run;

public sealed class RunStageContext
{
    public required string InitialGoal { get; init; }
    public required IReadOnlyCollection<Practice> Practices { get; init; }
    public required string? AgentsPath { get; init; }
    public required PromptOptRunContext PromptOptRunContext { get; init; }
    public required string BundleId { get; init; }
    public required string SourceRepositoryPath { get; init; }
    public required IAgentEventPublisher Events { get; init; }

    public string BaseCommit { get; set; } = string.Empty;
    public IReadOnlyCollection<string> SelectedPracticeNames { get; set; } = Array.Empty<string>();
    public IReadOnlyCollection<Practice> SelectedPractices { get; set; } = Array.Empty<Practice>();
    public string PracticesContext { get; set; } = string.Empty;
    public ITranscriptWriter TranscriptWriter { get; set; } = new NoOpTranscriptWriter();
    public string GitDiffContent { get; set; } = string.Empty;
    public RunResult? RunResult { get; set; }
    public Exception? TerminalException { get; set; }
}
