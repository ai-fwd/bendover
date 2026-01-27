using Bendover.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bendover.Application;

public class PracticeService : IPracticeService
{
    private readonly string _practicesPath;
    private readonly List<Practice> _practices;

    public PracticeService(string? practicesPath = null)
    {
        _practicesPath = practicesPath ?? BendoverPaths.GetPracticesPath();
        _practices = new List<Practice>();
        LoadPractices();
    }

    private void LoadPractices()
    {
        if (!Directory.Exists(_practicesPath))
        {
            Directory.CreateDirectory(_practicesPath);
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .Build();

        foreach (var file in Directory.GetFiles(_practicesPath, "*.md"))
        {
            var content = File.ReadAllText(file);
            var parts = content.Split(new[] { "---" }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                var frontMatter = parts[0];
                var body = string.Join("---", parts.Skip(1)).Trim();

                try
                {
                    var dto = deserializer.Deserialize<PracticeDto>(frontMatter);
                    if (dto != null)
                    {
                        // Handle mapping logic or ensure DTO matches expected types
                        // Parsing Enum manually if YamlDotNet fails or just ensure Enum matching
                        _practices.Add(new Practice(dto.Name, dto.TargetRole, dto.AreaOfConcern, body));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to parse practice {file}: {ex.Message}");
                }
            }
        }
    }

    private class PracticeDto
    {
        public string Name { get; set; } = string.Empty;
        public AgentRole TargetRole { get; set; }
        public string AreaOfConcern { get; set; } = string.Empty;
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
