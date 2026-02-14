using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Bendover.Infrastructure;
using Xunit;

namespace Bendover.Tests.Integration;

public class CliFullScenarioAcceptanceTests
{
    private const string RealLlmToggleEnvVar = "BENDOVER_ACCEPTANCE_USE_REAL_LLM";

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task RunAsync_ShouldExecuteCliToSandboxAndApplyPatch_WithMockLlmFallback()
    {
        await new DockerEnvironmentValidator().ValidateAsync();

        var scenario = AcceptanceScenario.Create("mock");
        await using var mockServer = new OpenAiCompatibleMockServer(scenario);

        var agentConfiguration = new AgentConfiguration(
            Model: "mock-model",
            Endpoint: $"{mockServer.BaseEndpoint}/v1",
            ApiKey: "mock-key");

        using var result = await RunScenarioAsync(scenario, agentConfiguration);

        Assert.True(
            result.CliExitCode == 0,
            $"CLI exited with code {result.CliExitCode}\nstdout:\n{result.CliStdout}\nstderr:\n{result.CliStderr}");
        AssertContainsOrThrow(result.OutputPhases, "lead", "Missing lead phase output.");
        // AssertContainsOrThrow(result.OutputPhases, "architect", "Missing architect phase output.");
        AssertContainsOrThrow(result.OutputPhases, "engineer", "Missing engineer phase output.");
        // AssertContainsOrThrow(result.OutputPhases, "reviewer", "Missing reviewer phase output.");

        Assert.True(File.Exists(result.GitDiffPath), $"Expected git diff artifact at {result.GitDiffPath}");
        var gitDiff = File.ReadAllText(result.GitDiffPath);
        Assert.Contains(scenario.TargetFilePath, gitDiff, StringComparison.Ordinal);
        Assert.Contains(scenario.MarkerLine, gitDiff, StringComparison.Ordinal);

        AssertBuildAndTestArtifactsExist(result.RunDirectoryPath);

        var patchedFilePath = Path.Combine(result.RepoClonePath, scenario.TargetFilePath);
        Assert.True(File.Exists(patchedFilePath), $"Expected patched file at {patchedFilePath}");
        var patchedFileContents = File.ReadAllText(patchedFilePath);
        Assert.Contains(scenario.MarkerLine, patchedFileContents, StringComparison.Ordinal);
        Assert.Contains(scenario.TargetFilePath, result.GitChangedPaths, StringComparer.Ordinal);
    }

    [Fact]
    [Trait("Category", "Acceptance")]
    public async Task RunAsync_ShouldExecuteCliToSandboxAndApplyPatch_WithRealLlm_WhenEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(RealLlmToggleEnvVar), "1", StringComparison.Ordinal))
        {
            return;
        }

        await new DockerEnvironmentValidator().ValidateAsync();

        var repositoryRoot = FindRepoRoot();
        var agentConfiguration = TryResolveAgentConfiguration(repositoryRoot);
        if (agentConfiguration is null)
        {
            return;
        }

        var scenario = AcceptanceScenario.Create("real");
        using var result = await RunScenarioAsync(scenario, agentConfiguration);

        Assert.True(
            result.CliExitCode == 0,
            $"CLI exited with code {result.CliExitCode}\nstdout:\n{result.CliStdout}\nstderr:\n{result.CliStderr}");
        AssertContainsOrThrow(result.OutputPhases, "lead", "Missing lead phase output.");
        // AssertContainsOrThrow(result.OutputPhases, "architect", "Missing architect phase output.");
        Assert.Contains("engineer", result.OutputPhases, StringComparer.OrdinalIgnoreCase);
        // Assert.Contains("reviewer", result.OutputPhases, StringComparer.OrdinalIgnoreCase);

        Assert.True(File.Exists(result.GitDiffPath), $"Expected git diff artifact at {result.GitDiffPath}");
        Assert.False(string.IsNullOrWhiteSpace(File.ReadAllText(result.GitDiffPath)));

        AssertBuildAndTestArtifactsExist(result.RunDirectoryPath);
        Assert.True(result.GitChangedPaths.Length > 0, "Expected repository changes after patch apply.");
    }

    private static async Task<AcceptanceRunResult> RunScenarioAsync(AcceptanceScenario scenario, AgentConfiguration agentConfiguration)
    {
        var repositoryRoot = FindRepoRoot();
        var configuration = ResolveConfiguration();
        var cliDllPath = EnsureProjectOutput(repositoryRoot, "Bendover.Presentation.CLI", configuration);

        var tempRoot = Path.Combine(Path.GetTempPath(), "bendover_cli_acceptance_" + Guid.NewGuid().ToString("N"));
        var repoClonePath = Path.Combine(tempRoot, "repo");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await RunProcessOrThrowAsync(
                "git",
                new[] { "clone", repositoryRoot, repoClonePath },
                repositoryRoot,
                timeout: TimeSpan.FromMinutes(2));

            await OverlayWorkingTreeChangesAsync(repositoryRoot, repoClonePath);

            var cloneEnvPath = Path.Combine(repoClonePath, ".env");
            if (File.Exists(cloneEnvPath))
            {
                File.Delete(cloneEnvPath);
            }

            var sourcePracticesPath = Path.Combine(repositoryRoot, ".bendover", "practices");
            var targetPracticesPath = Path.Combine(repoClonePath, ".bendover", "practices");
            if (Directory.Exists(sourcePracticesPath))
            {
                CopyDirectory(sourcePracticesPath, targetPracticesPath);
            }

            await CreateBaselineCommitIfNeededAsync(repoClonePath);

            var cliResult = await RunProcessAsync(
                "dotnet",
                new[] { cliDllPath, scenario.Goal },
                repoClonePath,
                environment: new Dictionary<string, string>
                {
                    ["Agent__Model"] = agentConfiguration.Model,
                    ["Agent__Endpoint"] = agentConfiguration.Endpoint,
                    ["Agent__ApiKey"] = agentConfiguration.ApiKey,
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
                },
                timeout: TimeSpan.FromMinutes(12));

            if (cliResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"CLI run failed with exit code {cliResult.ExitCode}.\nstdout:\n{cliResult.Stdout}\nstderr:\n{cliResult.Stderr}");
            }

            var runDirectoryPath = ResolveLatestRunDirectory(repoClonePath);
            var outputsPath = Path.Combine(runDirectoryPath, "outputs.json");
            Assert.True(File.Exists(outputsPath), $"Expected outputs.json at {outputsPath}");

            var outputPhases = ReadOutputPhases(outputsPath);
            var gitStatusResult = await RunProcessOrThrowAsync(
                "git",
                new[] { "status", "--porcelain" },
                repoClonePath,
                timeout: TimeSpan.FromMinutes(1));

            var changedPaths = ParseGitChangedPaths(gitStatusResult.Stdout);
            var gitDiffPath = Path.Combine(runDirectoryPath, "git_diff.patch");

            return new AcceptanceRunResult(
                tempRoot,
                repoClonePath,
                runDirectoryPath,
                gitDiffPath,
                outputPhases,
                changedPaths,
                cliResult.ExitCode,
                cliResult.Stdout,
                cliResult.Stderr);
        }
        catch
        {
            TryDeleteDirectory(tempRoot);
            throw;
        }
    }

    private static void AssertBuildAndTestArtifactsExist(string runDirectoryPath)
    {
        var buildSuccessPath = Path.Combine(runDirectoryPath, "dotnet_build.txt");
        var buildFailurePath = Path.Combine(runDirectoryPath, "dotnet_build_error.txt");
        Assert.True(
            File.Exists(buildSuccessPath) || File.Exists(buildFailurePath),
            $"Expected build artifact at {buildSuccessPath} or {buildFailurePath}");

        var testSuccessPath = Path.Combine(runDirectoryPath, "dotnet_test.txt");
        var testFailurePath = Path.Combine(runDirectoryPath, "dotnet_test_error.txt");
        Assert.True(
            File.Exists(testSuccessPath) || File.Exists(testFailurePath),
            $"Expected test artifact at {testSuccessPath} or {testFailurePath}");
    }

    private static HashSet<string> ReadOutputPhases(string outputsPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(outputsPath));
        return document.RootElement
            .EnumerateObject()
            .Select(x => x.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void AssertContainsOrThrow(HashSet<string> values, string expected, string message)
    {
        if (!values.Contains(expected))
        {
            throw new Xunit.Sdk.XunitException(message);
        }
    }

    private static string[] ParseGitChangedPaths(string gitStatusOutput)
    {
        return gitStatusOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line.Substring(3).Trim() : line.Trim())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string ResolveLatestRunDirectory(string repoClonePath)
    {
        var runRootPath = Path.Combine(repoClonePath, ".bendover", "promptopt", "runs");
        Assert.True(Directory.Exists(runRootPath), $"Expected run root directory at {runRootPath}");

        var runDirectories = Directory.GetDirectories(runRootPath);
        Assert.True(runDirectories.Length > 0, $"Expected at least one run directory under {runRootPath}");

        return runDirectories
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .First();
    }

    private static AgentConfiguration? TryResolveAgentConfiguration(string repositoryRoot)
    {
        var model = Environment.GetEnvironmentVariable("Agent__Model");
        var endpoint = Environment.GetEnvironmentVariable("Agent__Endpoint");
        var apiKey = Environment.GetEnvironmentVariable("Agent__ApiKey");

        if (!string.IsNullOrWhiteSpace(model) &&
            !string.IsNullOrWhiteSpace(endpoint) &&
            !string.IsNullOrWhiteSpace(apiKey))
        {
            return new AgentConfiguration(model, endpoint, apiKey);
        }

        var dotEnvPath = Path.Combine(repositoryRoot, ".env");
        if (!File.Exists(dotEnvPath))
        {
            return null;
        }

        var values = File.ReadAllLines(dotEnvPath)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#", StringComparison.Ordinal))
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);

        values.TryGetValue("Agent__Model", out model);
        values.TryGetValue("Agent__Endpoint", out endpoint);
        values.TryGetValue("Agent__ApiKey", out apiKey);

        if (string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        return new AgentConfiguration(model, endpoint, apiKey);
    }

    private static async Task<ProcessResult> RunProcessOrThrowAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput = null,
        IDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var result = await RunProcessAsync(fileName, arguments, workingDirectory, standardInput, environment, timeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Command failed: {fileName} {string.Join(' ', arguments)}\nstdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        }

        return result;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string? standardInput = null,
        IDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process {fileName}.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Best-effort kill.
            }

            throw new TimeoutException(
                $"Process timed out after {(timeout ?? TimeSpan.FromMinutes(10)).TotalMinutes:F1} minutes: {fileName} {string.Join(' ', arguments)}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
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
            RunProcessOrThrowAsync(
                    "dotnet",
                    new[] { "build", projectPath, "-c", configuration },
                    repoRoot,
                    timeout: TimeSpan.FromMinutes(5))
                .GetAwaiter()
                .GetResult();
        }

        if (!File.Exists(outputDll))
        {
            throw new InvalidOperationException($"Build output not found: {outputDll}");
        }

        return outputDll;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.GetFiles(sourceDirectory))
        {
            var targetFilePath = Path.Combine(targetDirectory, Path.GetFileName(file));
            File.Copy(file, targetFilePath, overwrite: true);
        }

        foreach (var sourceSubDirectory in Directory.GetDirectories(sourceDirectory))
        {
            var targetSubDirectory = Path.Combine(targetDirectory, Path.GetFileName(sourceSubDirectory));
            CopyDirectory(sourceSubDirectory, targetSubDirectory);
        }
    }

    private static async Task OverlayWorkingTreeChangesAsync(string sourceRoot, string cloneRoot)
    {
        var overlayPaths = await GetWorkingTreeOverlayPathsAsync(sourceRoot);
        foreach (var relativePath in overlayPaths)
        {
            var sourcePath = Path.Combine(sourceRoot, relativePath);
            var targetPath = Path.Combine(cloneRoot, relativePath);

            if (File.Exists(sourcePath))
            {
                var parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                File.Copy(sourcePath, targetPath, overwrite: true);
                continue;
            }

            if (Directory.Exists(sourcePath))
            {
                CopyDirectory(sourcePath, targetPath);
                continue;
            }

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
                continue;
            }

            if (Directory.Exists(targetPath))
            {
                Directory.Delete(targetPath, recursive: true);
            }
        }
    }

    private static async Task<HashSet<string>> GetWorkingTreeOverlayPathsAsync(string repositoryRoot)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        await AddPathsAsync(paths, repositoryRoot, new[] { "diff", "--name-only" });
        await AddPathsAsync(paths, repositoryRoot, new[] { "diff", "--name-only", "--cached" });
        await AddPathsAsync(paths, repositoryRoot, new[] { "ls-files", "--others", "--exclude-standard" });
        return paths;
    }

    private static async Task AddPathsAsync(HashSet<string> paths, string repositoryRoot, IReadOnlyList<string> gitArguments)
    {
        var result = await RunProcessOrThrowAsync("git", gitArguments, repositoryRoot, timeout: TimeSpan.FromMinutes(1));
        foreach (var line in result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                paths.Add(line);
            }
        }
    }

    private static async Task CreateBaselineCommitIfNeededAsync(string repositoryRoot)
    {
        await RunProcessOrThrowAsync("git", new[] { "config", "user.email", "acceptance@local.test" }, repositoryRoot, timeout: TimeSpan.FromMinutes(1));
        await RunProcessOrThrowAsync("git", new[] { "config", "user.name", "Acceptance Runner" }, repositoryRoot, timeout: TimeSpan.FromMinutes(1));
        await RunProcessOrThrowAsync("git", new[] { "add", "-A" }, repositoryRoot, timeout: TimeSpan.FromMinutes(1));

        var commitResult = await RunProcessAsync(
            "git",
            new[] { "commit", "-m", "acceptance baseline snapshot" },
            repositoryRoot,
            timeout: TimeSpan.FromMinutes(1));

        if (commitResult.ExitCode != 0 &&
            !commitResult.Stdout.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase) &&
            !commitResult.Stderr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Failed to create baseline commit.\nstdout:\n{commitResult.Stdout}\nstderr:\n{commitResult.Stderr}");
        }
    }

    private sealed record AgentConfiguration(string Model, string Endpoint, string ApiKey);

    private sealed record AcceptanceScenario(string Goal, string TargetFilePath, string MarkerLine)
    {
        public static AcceptanceScenario Create(string label)
        {
            var id = $"{label}_{Guid.NewGuid():N}";
            var targetFilePath = "docs/repro/story_07_patch_apply.md";
            var markerLine = $"- AcceptanceMarker:{id}";
            var goal = $"Append the exact line '{markerLine}' to '{targetFilePath}'. Return BODY-ONLY C# statements using sdk globals.";
            return new AcceptanceScenario(goal, targetFilePath, markerLine);
        }
    }

    private sealed class AcceptanceRunResult : IDisposable
    {
        public AcceptanceRunResult(
            string tempRootPath,
            string repoClonePath,
            string runDirectoryPath,
            string gitDiffPath,
            HashSet<string> outputPhases,
            string[] gitChangedPaths,
            int cliExitCode,
            string cliStdout,
            string cliStderr)
        {
            TempRootPath = tempRootPath;
            RepoClonePath = repoClonePath;
            RunDirectoryPath = runDirectoryPath;
            GitDiffPath = gitDiffPath;
            OutputPhases = outputPhases;
            GitChangedPaths = gitChangedPaths;
            CliExitCode = cliExitCode;
            CliStdout = cliStdout;
            CliStderr = cliStderr;
        }

        public string TempRootPath { get; }
        public string RepoClonePath { get; }
        public string RunDirectoryPath { get; }
        public string GitDiffPath { get; }
        public HashSet<string> OutputPhases { get; }
        public string[] GitChangedPaths { get; }
        public int CliExitCode { get; }
        public string CliStdout { get; }
        public string CliStderr { get; }

        public void Dispose()
        {
            TryDeleteDirectory(TempRootPath);
        }
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class OpenAiCompatibleMockServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts;
        private readonly Task _serveTask;
        private readonly AcceptanceScenario _scenario;

        public OpenAiCompatibleMockServer(AcceptanceScenario scenario)
        {
            _scenario = scenario;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();

            var port = GetAvailablePort();
            BaseEndpoint = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{BaseEndpoint}/");
            _listener.Start();
            _serveTask = Task.Run(() => ServeLoopAsync(_cts.Token));
        }

        public string BaseEndpoint { get; }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();

            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
                // Best effort.
            }

            try
            {
                await _serveTask;
            }
            catch
            {
                // Ignore shutdown race exceptions.
            }
            finally
            {
                _cts.Dispose();
            }
        }

        private async Task ServeLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }

                await HandleRequestAsync(context, cancellationToken);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
        {
            var response = context.Response;
            try
            {
                string requestBody;
                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                {
                    requestBody = await reader.ReadToEndAsync();
                }

                var assistantText = BuildAssistantResponse(requestBody);
                var path = context.Request.Url?.AbsolutePath ?? string.Empty;

                var payload = path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)
                    ? BuildResponsesPayload(assistantText)
                    : BuildChatCompletionPayload(assistantText);

                var bytes = Encoding.UTF8.GetBytes(payload);
                response.StatusCode = 200;
                response.ContentType = "application/json";
                response.ContentLength64 = bytes.Length;
                await response.OutputStream.WriteAsync(bytes, cancellationToken);
            }
            catch
            {
                response.StatusCode = 500;
            }
            finally
            {
                try
                {
                    response.OutputStream.Close();
                }
                catch
                {
                    // Best effort.
                }
            }
        }

        private string BuildAssistantResponse(string requestBody)
        {
            if (requestBody.Contains("Lead Agent", StringComparison.OrdinalIgnoreCase))
            {
                return "[\"agent_orchestration_flow\",\"clean_interfaces\"]";
            }

            if (requestBody.Contains("You are an Architect", StringComparison.OrdinalIgnoreCase))
            {
                return $"Plan: append marker '{_scenario.MarkerLine}' to '{_scenario.TargetFilePath}'.";
            }

            if (requestBody.Contains("You are an Engineer", StringComparison.OrdinalIgnoreCase))
            {
                var escapedPath = EscapeForCSharpString(_scenario.TargetFilePath);
                var escapedMarker = EscapeForCSharpString(_scenario.MarkerLine);
                return string.Join(
                    '\n',
                    $"sdk.WriteFile(\"{escapedPath}\", sdk.ReadFile(\"{escapedPath}\") + \"\\n\\n{escapedMarker}\\n\");",
                    $"Console.WriteLine(\"{escapedMarker}\");");
            }

            if (requestBody.Contains("You are a Reviewer", StringComparison.OrdinalIgnoreCase))
            {
                return "Looks good.";
            }

            return "[]";
        }

        private static string BuildChatCompletionPayload(string assistantText)
        {
            var payload = new
            {
                id = "chatcmpl_mock",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = "mock-model",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = assistantText },
                        finish_reason = "stop"
                    }
                },
                usage = new
                {
                    prompt_tokens = 1,
                    completion_tokens = 1,
                    total_tokens = 2
                }
            };

            return JsonSerializer.Serialize(payload);
        }

        private static string BuildResponsesPayload(string assistantText)
        {
            var payload = new
            {
                id = "resp_mock",
                @object = "response",
                created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                status = "completed",
                model = "mock-model",
                output = new[]
                {
                    new
                    {
                        type = "message",
                        id = "msg_mock",
                        status = "completed",
                        role = "assistant",
                        content = new[]
                        {
                            new
                            {
                                type = "output_text",
                                text = assistantText
                            }
                        }
                    }
                },
                usage = new
                {
                    input_tokens = 1,
                    output_tokens = 1,
                    total_tokens = 2
                }
            };

            return JsonSerializer.Serialize(payload);
        }

        private static string EscapeForCSharpString(string value)
        {
            return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        }

        private static int GetAvailablePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
