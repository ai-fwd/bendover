namespace Bendover.Application;

public interface IPromptBundleResolver
{
    string Resolve();
    string Resolve(string bundlePath);
    string? GetActiveBundleId();
}
