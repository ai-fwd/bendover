using Bendover.Application;
using Xunit;

namespace Bendover.Tests;

public class PromptBundleResolverTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly string _repoRoot;

    public PromptBundleResolverTests()
    {
        _tempDir = Directory.CreateTempSubdirectory("BendoverTests_");
        _repoRoot = _tempDir.FullName;
    }

    public void Dispose()
    {
        if (_tempDir.Exists)
        {
            try
            {
                _tempDir.Delete(true);
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    [Fact]
    public void Resolve_ShouldReturnDefaultPath_WhenActiveJsonMissing()
    {
        // Arrange
        var sut = new PromptBundleResolver(_repoRoot);

        // Act
        var result = sut.Resolve();

        // Assert
        var expected = Path.Combine(_repoRoot, ".bendover", "promptopt", "practices");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_ShouldReturnBundlePath_WhenActiveJsonExists()
    {
        // Arrange
        var promptOptDir = Directory.CreateDirectory(Path.Combine(_repoRoot, ".bendover", "promptopt"));
        var activeJsonPath = Path.Combine(promptOptDir.FullName, "active.json");
        File.WriteAllText(activeJsonPath, "{ \"bundleId\": \"test_bundle\" }");

        var sut = new PromptBundleResolver(_repoRoot);

        // Act
        var result = sut.Resolve();

        // Assert
        var expected = Path.Combine(_repoRoot, ".bendover", "promptopt", "bundles", "test_bundle", "practices");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Resolve_ShouldThrow_WhenActiveJsonExistsButBundleIdMissing()
    {
        // Arrange
        var promptOptDir = Directory.CreateDirectory(Path.Combine(_repoRoot, ".bendover", "promptopt"));
        var activeJsonPath = Path.Combine(promptOptDir.FullName, "active.json");
        File.WriteAllText(activeJsonPath, "{ }"); // Missing bundleId

        var sut = new PromptBundleResolver(_repoRoot);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => sut.Resolve());
        Assert.Contains("Missing bundleId", ex.Message);
    }

    [Fact]
    public void Resolve_ShouldNotWriteToDisk()
    {
        // Arrange
        var sut = new PromptBundleResolver(_repoRoot);

        // Capture specific state before
        var beforeState = _tempDir.GetFileSystemInfos("*", SearchOption.AllDirectories).Select(x => x.FullName).ToHashSet();

        // Act
        try
        {
            sut.Resolve();
        }
        catch
        {
            // Ignore logic errors, checking side effects
        }

        // Assert
        var afterState = _tempDir.GetFileSystemInfos("*", SearchOption.AllDirectories).Select(x => x.FullName).ToHashSet();

        // We expect no new files or directories created by Resolve() itself in the repo root
        // (Resolve() might fail if paths don't exist, but it definitely shouldn't create them)
        Assert.Empty(afterState.Except(beforeState));
    }
}
