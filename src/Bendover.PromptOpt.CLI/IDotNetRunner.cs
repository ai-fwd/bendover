using System.Threading.Tasks;

namespace Bendover.PromptOpt.CLI;

public interface IDotNetRunner
{
    Task<string> RunTestsAsync(string workingDirectory);
}
