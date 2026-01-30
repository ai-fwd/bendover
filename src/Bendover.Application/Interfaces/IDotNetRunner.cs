using System.Threading.Tasks;

namespace Bendover.Application.Interfaces;

public interface IDotNetRunner
{
    Task<string> RunAsync(string arguments, string? workingDirectory = null);
}
