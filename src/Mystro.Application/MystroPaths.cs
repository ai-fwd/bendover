namespace Mystro.Application;

internal static class MystroPaths
{
    private const string ApplicationProjectFile = "Mystro.Application.csproj";

    public static string GetApplicationRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".mystro")))
                {
                    return dir.FullName;
                }

                if (File.Exists(Path.Combine(dir.FullName, ApplicationProjectFile)))
                {
                    return dir.FullName;
                }

                var srcCandidate = Path.Combine(dir.FullName, "src", "Mystro.Application", ApplicationProjectFile);
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
        return Path.Combine(GetApplicationRoot(), ".mystro", "practices");
    }

    public static string GetSystemPromptPath(string promptName)
    {
        return Path.Combine(GetApplicationRoot(), ".mystro", "system_prompts", $"{promptName}.md");
    }
}
