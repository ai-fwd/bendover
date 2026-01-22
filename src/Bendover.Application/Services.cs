namespace Bendover.Application;

public class GovernanceEngine
{
    public Task<string> GetContextAsync()
    {
        // Stub: In real imp, read .md/yaml files from a 'WayOfCoding' dir
        return Task.FromResult(" गवर्नance: Always use clean architecture. No comments.");
    }
}

public class ScriptGenerator
{
    public string WrapCode(string rawCode)
    {
        // Wrap raw code in a .csx structure with references
        return $@"
#r ""Bendover.SDK.dll""
using Bendover.SDK;

{rawCode}
";
    }
}
