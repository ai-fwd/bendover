using System.Collections.Generic;

namespace Bendover.Application.Evaluation;

public record FileDiff(string Path, FileStatus Status, string Content);

public enum FileStatus
{
    Added,
    Modified,
    Deleted,
    Unknown
}

public record EvaluationContext(
    string DiffContent,
    string TestOutput,
    IEnumerable<FileDiff> ChangedFiles,
    IReadOnlyList<string>? SelectedPractices = null,
    IReadOnlyList<string>? AllPractices = null
);

public record RuleResult(
    string RuleName,
    bool Passed,
    float ScoreDelta,
    string[] Notes,
    bool IsHardFailure = false,
    string[]? AffectedPractices = null,
    Dictionary<string, string[]>? NotesByPractice = null
);

public record PracticeAttribution(
    string[] SelectedPractices,
    string[] OffendingPractices,
    Dictionary<string, string[]> NotesByPractice
);

public record FinalEvaluation(
    bool Pass,
    float Score,
    string[] Flags,
    string[] Notes,
    PracticeAttribution? PracticeAttribution = null
);
