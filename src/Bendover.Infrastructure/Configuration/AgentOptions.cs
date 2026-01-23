using Bendover.Domain;

namespace Bendover.Infrastructure.Configuration;

public class AgentOptions
{
    public const string SectionName = "Agent";

    public string? Model { get; set; }
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }

    public Dictionary<AgentRole, AgentOptions>? RoleOverrides { get; set; }
}
