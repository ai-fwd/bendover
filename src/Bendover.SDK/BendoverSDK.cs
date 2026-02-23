using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bendover.SDK;

public sealed class BendoverSDK
{
    private readonly ISdkActionEventSink _eventSink;

    public BendoverSDK(ISdkActionEventSink eventSink)
    {
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    }

    /// <summary>
    /// Writes file content to a workspace-relative path.
    /// </summary>
    /// <tool_category>mutation</tool_category>
    /// <use_instead_of>manual file redirection/edit scripting</use_instead_of>
    /// <result_visibility>Automatically emits write success/failure details including byte count.</result_visibility>
    /// <param_rule name="path">Must resolve inside workspace root.</param_rule>
    public void WriteFile(string path, string content)
    {
        ExecuteAction(
            action: () =>
            {
                var fullPath = ResolveWorkspacePath(path);
                var existedBefore = File.Exists(fullPath);
                var parentDirectory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }

                File.WriteAllText(fullPath, content);
                return new
                {
                    path = ToWorkspaceRelativePath(fullPath),
                    full_path = fullPath,
                    existed_before = existedBefore,
                    bytes_written = content.Length
                };
            });
    }

    /// <summary>
    /// Deletes a file at a workspace-relative path.
    /// </summary>
    /// <tool_category>mutation</tool_category>
    /// <use_instead_of>rm</use_instead_of>
    /// <result_visibility>Automatically emits delete success/failure details.</result_visibility>
    /// <param_rule name="path">Must resolve inside workspace root.</param_rule>
    public void DeleteFile(string path)
    {
        ExecuteAction(
            action: () =>
            {
                var fullPath = ResolveWorkspacePath(path);
                var existedBefore = File.Exists(fullPath);
                if (existedBefore)
                {
                    File.Delete(fullPath);
                }

                return new
                {
                    path = ToWorkspaceRelativePath(fullPath),
                    full_path = fullPath,
                    existed_before = existedBefore,
                    deleted = existedBefore && !File.Exists(fullPath)
                };
            });
    }

    /// <summary>
    /// Reads full file content from a workspace-relative path.
    /// </summary>
    /// <tool_category>discovery</tool_category>
    /// <use_instead_of>cat</use_instead_of>
    /// <result_visibility>Automatically emits full file content in success payload.</result_visibility>
    /// <param_rule name="path">Must resolve inside workspace root.</param_rule>
    public string ReadFile(string path)
    {
        return ExecuteAction(
            action: () =>
            {
                var fullPath = ResolveWorkspacePath(path);
                var content = File.ReadAllText(fullPath);
                return new ReadFileResult(
                    Path: ToWorkspaceRelativePath(fullPath),
                    FullPath: fullPath,
                    Bytes: content.Length,
                    Content: content);
            },
            returnSelector: result => result.Content);
    }

    /// <summary>
    /// Locates files by path substring or wildcard pattern.
    /// </summary>
    /// <tool_category>discovery</tool_category>
    /// <use_instead_of>find</use_instead_of>
    /// <result_visibility>Automatically emits full structured locate results.</result_visibility>
    /// <param_rule name="pattern">Substring by default; supports '*' and '?' wildcard patterns.</param_rule>
    public LocateFileResult LocateFile(string pattern, LocateFileOptions? options = null)
    {
        return ExecuteAction(
            action: () => LocateFileInternal(pattern, options),
            returnSelector: result => result);
    }

    /// <summary>
    /// Inspects file content using regex or plain-text matching.
    /// </summary>
    /// <tool_category>discovery</tool_category>
    /// <use_instead_of>rg, grep, sed</use_instead_of>
    /// <result_visibility>Automatically emits full structured match results with context lines.</result_visibility>
    /// <param_rule name="pattern">Regex by default; set UseRegex=false for literal matching.</param_rule>
    public InspectFileResult InspectFile(string pattern, InspectFileOptions? options = null)
    {
        return ExecuteAction(
            action: () => InspectFileInternal(pattern, options),
            returnSelector: result => result);
    }

    /// <summary>
    /// Runs dotnet build from the current workspace.
    /// </summary>
    /// <tool_category>verification</tool_category>
    /// <use_instead_of>dotnet build shell command</use_instead_of>
    /// <result_visibility>Automatically emits full stdout/stderr/exit code.</result_visibility>
    public void Build()
    {
        ExecuteAction(
            action: () => RunDotNetCommand("build", nameof(Build)));
    }

    /// <summary>
    /// Runs dotnet test from the current workspace.
    /// </summary>
    /// <tool_category>verification</tool_category>
    /// <use_instead_of>dotnet test shell command</use_instead_of>
    /// <result_visibility>Automatically emits full stdout/stderr/exit code.</result_visibility>
    public void Test()
    {
        ExecuteAction(
            action: () => RunDotNetCommand("test", nameof(Test)));
    }

    /// <summary>
    /// Lists changed files relative to the workspace root.
    /// </summary>
    /// <tool_category>repository_read</tool_category>
    /// <use_instead_of>git diff --name-only</use_instead_of>
    /// <result_visibility>Automatically emits the full changed-file list.</result_visibility>
    public string[] ListChangedFiles()
    {
        return ExecuteAction(
            action: () =>
            {
                var process = RunProcess("git", new[] { "diff", "--name-only" });
                EnsureProcessSucceeded(process, nameof(ListChangedFiles));
                return process.CombinedOutput
                    .Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            },
            returnSelector: result => result);
    }

    /// <summary>
    /// Returns git diff output. Optionally limits diff to one workspace path.
    /// </summary>
    /// <tool_category>repository_read</tool_category>
    /// <use_instead_of>git diff</use_instead_of>
    /// <result_visibility>Automatically emits full diff output.</result_visibility>
    /// <param_rule name="path">Optional workspace-relative path filter.</param_rule>
    public string GetDiff(string? path = null)
    {
        return ExecuteAction(
            action: () =>
            {
                var args = new List<string> { "diff" };
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var fullPath = ResolveWorkspacePath(path);
                    args.Add("--");
                    args.Add(ToWorkspaceRelativePath(fullPath));
                }

                var process = RunProcess("git", args);
                EnsureProcessSucceeded(process, nameof(GetDiff));
                return process.CombinedOutput;
            },
            returnSelector: result => result);
    }

    /// <summary>
    /// Returns current git HEAD commit hash.
    /// </summary>
    /// <tool_category>repository_read</tool_category>
    /// <use_instead_of>git rev-parse HEAD</use_instead_of>
    /// <result_visibility>Automatically emits commit hash output.</result_visibility>
    public string GetHeadCommit()
    {
        return ExecuteAction(
            action: () =>
            {
                var process = RunProcess("git", new[] { "rev-parse", "HEAD" });
                EnsureProcessSucceeded(process, nameof(GetHeadCommit));
                return process.CombinedOutput.Trim();
            },
            returnSelector: result => result);
    }

    /// <summary>
    /// Returns current git branch name.
    /// </summary>
    /// <tool_category>repository_read</tool_category>
    /// <use_instead_of>git rev-parse --abbrev-ref HEAD</use_instead_of>
    /// <result_visibility>Automatically emits branch name output.</result_visibility>
    public string GetCurrentBranch()
    {
        return ExecuteAction(
            action: () =>
            {
                var process = RunProcess("git", new[] { "rev-parse", "--abbrev-ref", "HEAD" });
                EnsureProcessSucceeded(process, nameof(GetCurrentBranch));
                return process.CombinedOutput.Trim();
            },
            returnSelector: result => result);
    }

    /// <summary>
    /// Signals that the agent is done and the host loop should stop.
    /// </summary>
    /// <tool_category>completion</tool_category>
    /// <use_instead_of>custom completion shell signaling</use_instead_of>
    /// <result_visibility>Automatically emits completion signal metadata.</result_visibility>
    public void Done()
    {
        ExecuteAction(
            action: () => new { done = true });
    }

    private static LocateFileResult LocateFileInternal(string pattern, LocateFileOptions? options)
    {
        var effectiveOptions = options ?? new LocateFileOptions();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new InvalidOperationException("LocateFile pattern cannot be empty.");
        }

        var matches = new List<string>();
        var maxResults = Math.Max(1, effectiveOptions.MaxResults);
        var wildcardPattern = pattern.Contains('*') || pattern.Contains('?')
            ? pattern
            : null;

        foreach (var relativePath in EnumerateWorkspaceFiles())
        {
            if (!ShouldIncludePath(relativePath, effectiveOptions.IncludeGlobs, effectiveOptions.ExcludeGlobs))
            {
                continue;
            }

            var pathMatched = wildcardPattern is not null
                ? WildcardMatch(relativePath, wildcardPattern, effectiveOptions.CaseSensitive)
                : relativePath.Contains(
                    pattern,
                    effectiveOptions.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (!pathMatched)
            {
                continue;
            }

            matches.Add(relativePath);
            if (matches.Count >= maxResults)
            {
                break;
            }
        }

        var totalMatches = matches.Count;
        return new LocateFileResult(
            Pattern: pattern,
            Matches: matches,
            TotalMatches: totalMatches,
            Truncated: totalMatches >= maxResults);
    }

    private static InspectFileResult InspectFileInternal(string pattern, InspectFileOptions? options)
    {
        var effectiveOptions = options ?? new InspectFileOptions();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new InvalidOperationException("InspectFile pattern cannot be empty.");
        }

        var files = ResolveInspectCandidateFiles(effectiveOptions);
        var matches = new List<InspectFileMatch>();
        var maxMatches = Math.Max(1, effectiveOptions.MaxMatches);
        var contextLines = Math.Max(0, effectiveOptions.ContextLines);

        Regex? regex = null;
        if (effectiveOptions.UseRegex)
        {
            var regexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (effectiveOptions.IgnoreCase)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            regex = new Regex(pattern, regexOptions);
        }

        foreach (var relativePath in files)
        {
            var fullPath = ResolveWorkspacePath(relativePath);
            string[] lines;
            try
            {
                lines = File.ReadAllLines(fullPath);
            }
            catch
            {
                continue;
            }

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                var isMatch = regex is not null
                    ? regex.IsMatch(line)
                    : line.Contains(
                        pattern,
                        effectiveOptions.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
                if (!isMatch)
                {
                    continue;
                }

                var before = CollectContext(lines, lineIndex - contextLines, lineIndex - 1);
                var after = CollectContext(lines, lineIndex + 1, lineIndex + contextLines);
                matches.Add(new InspectFileMatch(
                    Path: relativePath,
                    LineNumber: lineIndex + 1,
                    Line: line,
                    Before: before,
                    After: after));

                if (matches.Count >= maxMatches)
                {
                    return new InspectFileResult(
                        Pattern: pattern,
                        Matches: matches,
                        TotalMatches: matches.Count,
                        Truncated: true);
                }
            }
        }

        return new InspectFileResult(
            Pattern: pattern,
            Matches: matches,
            TotalMatches: matches.Count,
            Truncated: false);
    }

    private static List<string> ResolveInspectCandidateFiles(InspectFileOptions options)
    {
        if (options.Paths is { Length: > 0 })
        {
            var resolved = new List<string>();
            foreach (var path in options.Paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var fullPath = ResolveWorkspacePath(path);
                if (!File.Exists(fullPath))
                {
                    continue;
                }

                resolved.Add(ToWorkspaceRelativePath(fullPath));
            }

            return resolved;
        }

        var files = new List<string>();
        foreach (var relativePath in EnumerateWorkspaceFiles())
        {
            if (!ShouldIncludePath(relativePath, options.IncludeGlobs, options.ExcludeGlobs))
            {
                continue;
            }

            files.Add(relativePath);
        }

        return files;
    }

    private static List<string> CollectContext(string[] lines, int start, int end)
    {
        var context = new List<string>();
        if (start > end)
        {
            return context;
        }

        var boundedStart = Math.Max(0, start);
        var boundedEnd = Math.Min(lines.Length - 1, end);
        for (var index = boundedStart; index <= boundedEnd; index++)
        {
            context.Add(lines[index]);
        }

        return context;
    }

    private static IEnumerable<string> EnumerateWorkspaceFiles()
    {
        var root = WorkspaceRoot;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                var name = Path.GetFileName(directory);
                if (string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                pending.Push(directory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return ToWorkspaceRelativePath(file);
            }
        }
    }

    private static bool ShouldIncludePath(string relativePath, string[]? includeGlobs, string[]? excludeGlobs)
    {
        var include = includeGlobs is null || includeGlobs.Length == 0 || includeGlobs.Any(glob => WildcardMatch(relativePath, glob, caseSensitive: false));
        if (!include)
        {
            return false;
        }

        var excluded = excludeGlobs is not null && excludeGlobs.Any(glob => WildcardMatch(relativePath, glob, caseSensitive: false));
        return !excluded;
    }

    private static bool WildcardMatch(string input, string pattern, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);
        var regexOptions = RegexOptions.CultureInvariant;
        if (!caseSensitive)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        return Regex.IsMatch(input, $"^{escaped}$", regexOptions);
    }

    private static object RunDotNetCommand(
        string verb,
        string methodName)
    {
        var process = RunProcess("dotnet", new[] { verb });
        EnsureProcessSucceeded(process, methodName);
        return new
        {
            exit_code = process.ExitCode,
            stdout = process.Stdout,
            stderr = process.Stderr,
            combined_output = process.CombinedOutput
        };
    }

    private static void EnsureProcessSucceeded(
        ProcessRunResult process,
        string methodName)
    {
        if (process.ExitCode == 0)
        {
            return;
        }

        var actionName = string.IsNullOrWhiteSpace(methodName) ? "SDK action" : methodName;
        throw new InvalidOperationException(
            $"{actionName} failed with exit code {process.ExitCode}.\n{process.CombinedOutput}");
    }

    private static ProcessRunResult RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = WorkspaceRoot
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessRunResult(
            ExitCode: process.ExitCode,
            Stdout: stdout,
            Stderr: stderr,
            CombinedOutput: string.Concat(stdout, stderr));
    }

    private void ExecuteAction(
        Func<object?> action,
        [CallerMemberName] string methodName = "")
    {
        ExecuteAction(
            action,
            returnSelector: static _ => (object?)null,
            methodName: methodName);
    }

    private TResult ExecuteAction<TPayload, TResult>(
        Func<TPayload> action,
        Func<TPayload, TResult> returnSelector,
        [CallerMemberName] string methodName = "")
    {
        var effectiveMethodName = string.IsNullOrWhiteSpace(methodName)
            ? nameof(BendoverSDK)
            : methodName;
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        EmitEvent(
            new SdkActionEvent(
                EventType: SdkActionEventType.Start,
                MethodName: effectiveMethodName,
                StartedAtUtc: startedAt,
                ElapsedMs: 0,
                PayloadJson: null,
                Error: null));

        try
        {
            var payload = action();
            stopwatch.Stop();
            EmitEvent(
                new SdkActionEvent(
                    EventType: SdkActionEventType.Success,
                    MethodName: effectiveMethodName,
                    StartedAtUtc: startedAt,
                    ElapsedMs: stopwatch.ElapsedMilliseconds,
                    PayloadJson: SerializePayload(payload),
                    Error: null));
            return returnSelector(payload);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            EmitEvent(
                new SdkActionEvent(
                    EventType: SdkActionEventType.Failure,
                    MethodName: effectiveMethodName,
                    StartedAtUtc: startedAt,
                    ElapsedMs: stopwatch.ElapsedMilliseconds,
                    PayloadJson: null,
                    Error: new SdkActionError(
                        Type: ex.GetType().FullName ?? "System.Exception",
                        Message: ex.Message,
                        StackTrace: ex.ToString())));
            throw;
        }
    }

    private void EmitEvent(SdkActionEvent sdkEvent)
    {
        _eventSink.OnEvent(sdkEvent);
    }

    private static string? SerializePayload<TPayload>(TPayload payload)
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(payload);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { serialization_error = ex.Message });
        }
    }

    private static string ResolveWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Path cannot be empty.");
        }

        var candidate = Path.IsPathRooted(path)
            ? path
            : Path.Combine(WorkspaceRoot, path);
        var fullPath = Path.GetFullPath(candidate);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var workspacePrefix = EnsureTrailingSeparator(WorkspaceRoot);

        if (!fullPath.StartsWith(workspacePrefix, comparison)
            && !string.Equals(fullPath, WorkspaceRoot, comparison))
        {
            throw new InvalidOperationException($"Path '{path}' resolves outside workspace root.");
        }

        return fullPath;
    }

    private static string ToWorkspaceRelativePath(string fullPath)
    {
        var relative = Path.GetRelativePath(WorkspaceRoot, fullPath);
        return relative.Replace('\\', '/');
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar)
            || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return path;
        }

        return path + Path.DirectorySeparatorChar;
    }

    private static string WorkspaceRoot => Path.GetFullPath(Directory.GetCurrentDirectory());

    private sealed record ProcessRunResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        string CombinedOutput);
}

public sealed record LocateFileOptions(
    string[]? IncludeGlobs = null,
    string[]? ExcludeGlobs = null,
    bool CaseSensitive = false,
    int MaxResults = 200);

public sealed record InspectFileOptions(
    string[]? Paths = null,
    string[]? IncludeGlobs = null,
    string[]? ExcludeGlobs = null,
    bool UseRegex = true,
    bool IgnoreCase = true,
    int ContextLines = 2,
    int MaxMatches = 200);

public sealed record ReadFileResult(
    string Path,
    string FullPath,
    int Bytes,
    string Content);

public sealed record LocateFileResult(
    string Pattern,
    IReadOnlyList<string> Matches,
    int TotalMatches,
    bool Truncated);

public sealed record InspectFileResult(
    string Pattern,
    IReadOnlyList<InspectFileMatch> Matches,
    int TotalMatches,
    bool Truncated);

public sealed record InspectFileMatch(
    string Path,
    int LineNumber,
    string Line,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After);
