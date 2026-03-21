using System.Threading.Tasks;

namespace Mystro.Application.Interfaces;

public interface IDotNetRunner
{
    Task<string> RunAsync(string arguments, string? workingDirectory = null);
}
