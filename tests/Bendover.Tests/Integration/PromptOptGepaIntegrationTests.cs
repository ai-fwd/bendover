using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Bendover.Tests.Integration;

public class PromptOptGepaIntegrationTests
{
    [Fact]
    public void RunGepa_MutatesPracticeFromStubbedFeedback()
    {
        var repoRoot = FindRepoRoot();
        var tempRoot = Path.Combine(Path.GetTempPath(), "bendover_gepa_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var promptOptRoot = Path.Combine(tempRoot, ".bendover", "promptopt");
            var bundlesRoot = Path.Combine(promptOptRoot, "bundles");
            var runsRoot = Path.Combine(promptOptRoot, "runs");
            var datasetsRoot = Path.Combine(promptOptRoot, "datasets");
            var logsRoot = Path.Combine(promptOptRoot, "logs");
            var cacheRoot = Path.Combine(promptOptRoot, "cache");
            Directory.CreateDirectory(bundlesRoot);
            Directory.CreateDirectory(runsRoot);
            Directory.CreateDirectory(datasetsRoot);
            Directory.CreateDirectory(logsRoot);

            var seedBundleId = "seed";
            var seedPracticesDir = Path.Combine(bundlesRoot, seedBundleId, "practices");
            Directory.CreateDirectory(seedPracticesDir);

            var practiceBody = "ORIGINAL";
            var practiceContent = "---\nName: simple_practice\nTargetRole: Engineer\nAreaOfConcern: Test\n---\n\n" + practiceBody;
            var practicePath = Path.Combine(seedPracticesDir, "simple_practice.md");
            File.WriteAllText(practicePath, practiceContent);

            var activeJsonPath = Path.Combine(promptOptRoot, "active.json");
            File.WriteAllText(activeJsonPath, "{\"bundleId\":\"seed\"}");

            var runId = "run-1";
            var runDir = Path.Combine(runsRoot, runId);
            Directory.CreateDirectory(runDir);
            File.WriteAllText(Path.Combine(runDir, "goal.txt"), "Do the thing");
            File.WriteAllText(Path.Combine(runDir, "base_commit.txt"), "abc123");

            var trainTxt = Path.Combine(datasetsRoot, "train.txt");
            File.WriteAllText(trainTxt, runId);

            var stubEvalPath = Path.Combine(tempRoot, "stub_eval.json");
            var stubJson = "{\n" +
                           "  \"pass\": false,\n" +
                           "  \"score\": 0.2,\n" +
                           "  \"flags\": [],\n" +
                           "  \"notes\": [\"Needs improvement\"],\n" +
                           "  \"practice_attribution\": {\n" +
                           "    \"selected_practices\": [\"simple_practice\"],\n" +
                           "    \"offending_practices\": [\"simple_practice\"],\n" +
                           "    \"notes_by_practice\": {\n" +
                           "      \"simple_practice\": [\"ADD_LINE: include_reflected_line\"]\n" +
                           "    }\n" +
                           "  },\n" +
                           "  \"score_if_contains\": {\n" +
                           "    \"file\": \"simple_practice.md\",\n" +
                           "    \"needle\": \"include_reflected_line\",\n" +
                           "    \"score\": 0.9\n" +
                           "  }\n" +
                           "}\n";
            File.WriteAllText(stubEvalPath, stubJson);

            var cliDll = EnsureProjectOutput(repoRoot, "Bendover.PromptOpt.CLI", ResolveConfiguration());
            var pythonExe = Path.Combine(repoRoot, "src", "promptopt", ".venv", "bin", "python");
            if (!File.Exists(pythonExe))
            {
                throw new InvalidOperationException($"Python venv not found at {pythonExe}. Run setup.sh to create it.");
            }

            var cliCommand = $"dotnet \"{cliDll}\" --stub-eval-json \"{stubEvalPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("promptopt.run_gepa");
            startInfo.ArgumentList.Add("--train-split");
            startInfo.ArgumentList.Add(trainTxt);
            startInfo.ArgumentList.Add("--log-dir");
            startInfo.ArgumentList.Add(logsRoot);
            startInfo.ArgumentList.Add("--cli-command");
            startInfo.ArgumentList.Add(cliCommand);
            startInfo.ArgumentList.Add("--bundle-root");
            startInfo.ArgumentList.Add(bundlesRoot);
            startInfo.ArgumentList.Add("--run-root");
            startInfo.ArgumentList.Add(runsRoot);
            startInfo.ArgumentList.Add("--active-json");
            startInfo.ArgumentList.Add(activeJsonPath);
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(bundlesRoot);
            startInfo.ArgumentList.Add("--cache-root");
            startInfo.ArgumentList.Add(cacheRoot);
            startInfo.ArgumentList.Add("--max-full-evals");
            startInfo.ArgumentList.Add("4");

            var pythonPath = Path.Combine(repoRoot, "src");
            startInfo.Environment["PYTHONPATH"] = pythonPath;
            startInfo.Environment["DSPY_CACHE_DIR"] = Path.Combine(tempRoot, ".dspy_cache");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start run_gepa process.");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"run_gepa failed.\nstdout:\n{stdout}\nstderr:\n{stderr}");

            var updatedActive = JsonDocument.Parse(File.ReadAllText(activeJsonPath));
            var bundleId = updatedActive.RootElement.GetProperty("bundleId").GetString();

            Assert.NotNull(bundleId);
            Assert.NotEqual(seedBundleId, bundleId);

            var mutatedPractice = Path.Combine(bundlesRoot, bundleId!, "practices", "simple_practice.md");
            var mutatedContent = File.ReadAllText(mutatedPractice);
            Assert.Contains("include_reflected_line", mutatedContent);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bendover.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate Bendover.sln from test base directory.");
    }

    private static string ResolveConfiguration()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var releaseToken = $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}";
        var debugToken = $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}";

        if (baseDirectory.Contains(releaseToken, StringComparison.OrdinalIgnoreCase))
        {
            return "Release";
        }

        if (baseDirectory.Contains(debugToken, StringComparison.OrdinalIgnoreCase))
        {
            return "Debug";
        }

        return "Debug";
    }

    private static string EnsureProjectOutput(string repoRoot, string projectName, string configuration)
    {
        var projectDirectory = Path.Combine(repoRoot, "src", projectName);
        var projectPath = Path.Combine(projectDirectory, $"{projectName}.csproj");
        var outputDirectory = Path.Combine(projectDirectory, "bin", configuration, "net10.0");
        var outputDll = Path.Combine(outputDirectory, $"{projectName}.dll");

        if (!File.Exists(outputDll))
        {
            RunProcess("dotnet", $"build \"{projectPath}\" -c {configuration}", repoRoot);
        }

        if (!File.Exists(outputDll))
        {
            throw new InvalidOperationException($"Build output not found: {outputDll}");
        }

        return outputDll;
    }

    private static void RunProcess(string fileName, string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new InvalidOperationException($"Failed to start process: {fileName}");
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Command failed: {fileName} {arguments}\n{stdout}\n{stderr}");
        }
    }
}
