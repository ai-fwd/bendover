using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace Bendover.Application.Interfaces;

public interface IPromptOptRunRecorder
{
    // Returns run_id
    Task<string> StartRunAsync(string goal, string baseCommit, string bundleId);
    Task RecordPracticesAsync(IEnumerable<string> practiceNames, string context);
    Task RecordPromptAsync(string phase, List<ChatMessage> messages);
    Task RecordOutputAsync(string phase, string output);
    Task FinalizeRunAsync();
}
