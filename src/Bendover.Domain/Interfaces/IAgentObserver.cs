namespace Bendover.Domain.Interfaces;

public interface IAgentObserver
{
    Task OnProgressAsync(string message);
}
