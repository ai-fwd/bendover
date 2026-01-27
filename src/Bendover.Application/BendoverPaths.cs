namespace Bendover.Application;

internal static class BendoverPaths
{
    private const string ApplicationProjectFile = "Bendover.Application.csproj";

    public static string GetApplicationRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, ApplicationProjectFile)))
                {
                    return dir.FullName;
                }

                var srcCandidate = Path.Combine(dir.FullName, "src", "Bendover.Application", ApplicationProjectFile);
                if (File.Exists(srcCandidate))
                {
                    return Path.GetDirectoryName(srcCandidate)!;
                }

                dir = dir.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }

    public static string GetPracticesPath()
    {
        return Path.Combine(GetApplicationRoot(), ".bendover", "practices");
    }

    public static string GetSystemPromptPath(string promptName)
    {
        return Path.Combine(GetApplicationRoot(), ".bendover", "system_prompts", $"{promptName}.md");
    }
}
