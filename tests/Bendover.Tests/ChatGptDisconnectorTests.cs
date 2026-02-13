using Bendover.Infrastructure.ChatGpt;
using Bendover.Presentation.CLI;

namespace Bendover.Tests;

public sealed class ChatGptDisconnectorTests : IDisposable
{
    private readonly string _tempRoot;

    public ChatGptDisconnectorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "bendover_disconnect_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Run_ClearsPersistedSession()
    {
        var store = new ChatGptAuthStore(_tempRoot);
        store.Save(new ChatGptAuthSession("access", "refresh", DateTimeOffset.UtcNow.AddHours(1), "acct", "user@example.com"));
        Assert.True(File.Exists(store.AuthFilePath));

        var sut = new ChatGptDisconnector(store);
        sut.Run();

        Assert.False(File.Exists(store.AuthFilePath));
    }

    [Fact]
    public void Run_IsIdempotent_WhenNoSessionExists()
    {
        var store = new ChatGptAuthStore(_tempRoot);
        var sut = new ChatGptDisconnector(store);

        sut.Run();
        sut.Run();

        Assert.False(File.Exists(store.AuthFilePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
