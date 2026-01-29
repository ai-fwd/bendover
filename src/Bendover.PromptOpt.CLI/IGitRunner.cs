using System.Threading.Tasks;

namespace Bendover.PromptOpt.CLI;

public interface IGitRunner
{
    Task CheckoutAsync(string commitHash, string workingDirectory);
    Task<string> GetDiffAsync(string workingDirectory);
}
