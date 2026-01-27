using Bendover.Domain;
using Bendover.Domain.Interfaces;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Bendover.Application;

public class PracticeService : IPracticeService
{
    private readonly string _practicesPath;
    private readonly IFileService _fileService;
    private readonly List<Practice> _practices;

    public PracticeService(IFileService fileService, string? practicesPath = null)
    {
        _fileService = fileService;
        _practicesPath = practicesPath ?? BendoverPaths.GetPracticesPath();
        _practices = new List<Practice>();
    }

    // Lazy load or explicit load? The previous implementation loaded in constructor.
    // Making it async-friendly or just loading on first request is better, but to match interface let's keep it simple.
    // However, since we now inject a service, we should probably load on demand or Initialize.
    // I'll load on first access or in constructor if synchronous. 
    // The previous one called LoadPractices() in constructor.
    // Let's call LoadPractices() in GetPracticesAsync to be safe and allow for transient errors or changes, or just cache it.
    // For now, I'll load in GetPracticesAsync if empty, or just reload. Let's stick to simple caching.

    public async Task<IEnumerable<Practice>> GetPracticesAsync()
    {
        if (_practices.Count == 0)
        {
            LoadPractices();
        }
        return await Task.FromResult(_practices);
    }

    private void LoadPractices()
    {
        if (!_fileService.DirectoryExists(_practicesPath))
        {
            // Optionally create it or just return empty
            // _fileService.CreateDirectory(_practicesPath); // Maybe don't auto-create in a query method
            return;
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(PascalCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        foreach (var file in _fileService.GetFiles(_practicesPath, "*.md"))
        {
            var content = _fileService.ReadAllText(file);
            var parts = content.Split(new[] { "---" }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
            {
                var frontMatter = parts[0];
                var body = string.Join("---", parts.Skip(1)).Trim();

                try
                {
                    // deserialize to a temp object to map to record
                    var temp = deserializer.Deserialize<PracticeMetadata>(frontMatter);
                    if (temp != null)
                    {
                        _practices.Add(new Practice(temp.Name, temp.TargetRole, temp.AreaOfConcern, body));
                    }
                }
                catch (Exception ex)
                {
                    // Logging?
                    Console.WriteLine($"Failed to parse practice {file}: {ex.Message}");
                }
            }
        }
    }

    private class PracticeMetadata
    {
        public string Name { get; set; } = string.Empty;
        public AgentRole TargetRole { get; set; }
        public string AreaOfConcern { get; set; } = string.Empty;
    }

    public async Task<IEnumerable<Practice>> GetPracticesForRoleAsync(AgentRole role)
    {
        var all = await GetPracticesAsync();
        return all.Where(p => p.TargetRole == role);
    }
}
