using System.Text.Json;

namespace Bendover.Application;

public class PromptBundleResolver : IPromptBundleResolver
{
    private readonly string _repoRoot;

    public PromptBundleResolver(string repoRoot)
    {
        _repoRoot = repoRoot;
    }

    public string Resolve()
    {
        var promptOptPath = Path.Combine(_repoRoot, ".bendover", "promptopt");
        var activeJsonPath = Path.Combine(promptOptPath, "active.json");

        if (!File.Exists(activeJsonPath))
        {
            return Path.Combine(promptOptPath, "practices");
        }

        var jsonContent = File.ReadAllText(activeJsonPath);

        // Basic parsing
        string? bundleId = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonContent);
            if (doc.RootElement.TryGetProperty("bundleId", out var element))
            {
                bundleId = element.GetString();
            }
        }
        catch (JsonException)
        {
            // If json is invalid, we treat it as error or just let it propagate? 
            // Requirement: THROW a clear error
            throw new InvalidOperationException($"Invalid JSON in {activeJsonPath}");
        }

        if (string.IsNullOrWhiteSpace(bundleId))
        {
            throw new InvalidOperationException($"active.json at {activeJsonPath} is Valid JSON but Missing bundleId.");
        }

        return Path.Combine(promptOptPath, "bundles", bundleId, "practices");
    }

    public string Resolve(string bundlePath)
    {
        // Bundle contract:
        // - practices/*.md
        // - meta.json

        // Return the practices directory within the bundle
        return Path.Combine(bundlePath, "practices");
    }
}
