namespace Bendover.Domain;

public interface IPracticeService
{
    Task<IEnumerable<Practice>> GetPracticesAsync();
    Task<IEnumerable<Practice>> GetPracticesForRoleAsync(AgentRole role);
}
