using System.Threading.Tasks;

namespace Bendover.PromptOpt.CLI;

public class StubAgentRunner : IAgentRunner
{
    public Task<AgentResult> RunAsync(string workingDirectory, string practicesPath, string taskText)
    {
        // Stub: In real implementation this would invoke the Agent logic.
        // For now, checks out OK.
        return Task.FromResult(new AgentResult(
            Success: true,
            Output: "Stub Agent execution completed successfully.",
            ArtifactsPath: workingDirectory
        ));
    }
}
