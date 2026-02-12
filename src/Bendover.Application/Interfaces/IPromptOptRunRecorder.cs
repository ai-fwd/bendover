using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Interfaces;

public interface IPromptOptRunRecorder
{
    // Returns run_id
    Task<string> StartRunAsync(string goal, string baseCommit, string bundleId);

    Task RecordPromptAsync(string phase, List<ChatMessage> messages);
    Task RecordOutputAsync(string phase, string output);
    Task RecordArtifactAsync(string filename, string content);
    Task FinalizeRunAsync();
}

public interface IPromptOptRunContextAccessor
{
    PromptOptRunContext? Current { get; set; }
}

public record PromptOptRunContext(
    string OutDir,
    bool Capture,
    string? RunId = null,
    string? BundleId = null,
    bool ApplySandboxPatchToSource = true,
    string PracticesRootRelativePath = ".bendover/practices"
);

public interface IPromptOptRunEvaluator
{
    Task EvaluateAsync(string outDir, string bundlePath);
}
