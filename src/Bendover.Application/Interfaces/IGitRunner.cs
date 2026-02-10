using System.Threading.Tasks;

namespace Bendover.Application.Interfaces;

public interface IGitRunner
{
    Task<string> RunAsync(string arguments, string? workingDirectory = null, string? standardInput = null);
}
