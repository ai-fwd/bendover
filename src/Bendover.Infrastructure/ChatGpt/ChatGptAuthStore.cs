using System.Text.Json;

namespace Bendover.Infrastructure.ChatGpt;

public sealed class ChatGptAuthStore
{
    private const string AuthFileName = "chatgpt.json";
    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ChatGptAuthStore()
        : this(GetDefaultRootPath())
    {
    }

    public ChatGptAuthStore(string rootPath)
    {
        RootPath = rootPath;
        AuthFilePath = Path.Combine(RootPath, AuthFileName);
    }

    public string RootPath { get; }
    public string AuthFilePath { get; }

    public ChatGptAuthSession? Load()
    {
        if (!File.Exists(AuthFilePath))
        {
            return null;
        }

        var json = File.ReadAllText(AuthFilePath);
        var session = JsonSerializer.Deserialize<ChatGptAuthSession>(json, SerializerOptions);
        if (session == null)
        {
            return null;
        }

        return session;
    }

    public void Save(ChatGptAuthSession session)
    {
        Directory.CreateDirectory(RootPath);
        var json = JsonSerializer.Serialize(session, SerializerOptions);
        File.WriteAllText(AuthFilePath, json);
    }

    public void Clear()
    {
        if (File.Exists(AuthFilePath))
        {
            File.Delete(AuthFilePath);
        }
    }

    private static string GetDefaultRootPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".bendover");
    }
}
