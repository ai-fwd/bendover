using System.Collections.Generic;

namespace Bendover.PromptOpt.CLI.Evaluation;

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
    IEnumerable<FileDiff> ChangedFiles
);

public record RuleResult(
    string RuleName,
    bool Passed,
    float ScoreDelta,
    string[] Notes,
    bool IsHardFailure = false
);

public record FinalEvaluation(
    bool Pass,
    float Score,
    string[] Flags,
    string[] Notes
);
