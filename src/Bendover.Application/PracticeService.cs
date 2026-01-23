using Bendover.Domain;

namespace Bendover.Application;

public class PracticeService : IPracticeService
{
    private readonly List<Practice> _practices;

    public PracticeService()
    {
        _practices = new List<Practice>
        {
            new Practice("tdd_spirit", AgentRole.Architect, "Architecture", "Write tests before code."),
            new Practice("clean_interfaces", AgentRole.Engineer, "Code Style", "Keep interfaces small and focused."),
            new Practice("readme_hygiene", AgentRole.Reviewer, "Documentation", "Ensure README is up to date.")
        };
    }

    public Task<IEnumerable<Practice>> GetPracticesAsync()
    {
        return Task.FromResult<IEnumerable<Practice>>(_practices);
    }

    public Task<IEnumerable<Practice>> GetPracticesForRoleAsync(AgentRole role)
    {
        var filtered = _practices.Where(p => p.TargetRole == role);
        return Task.FromResult(filtered);
    }
}
