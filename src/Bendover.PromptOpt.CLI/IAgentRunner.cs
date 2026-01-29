using System.Threading.Tasks;

namespace Bendover.PromptOpt.CLI;

public record AgentResult(bool Success, string Output, string ArtifactsPath);

public interface IAgentRunner
{
    Task<AgentResult> RunAsync(string workingDirectory, string practicesPath, string taskText);
}
