using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Bendover.Application.Evaluation;

public static class DiffParser
{
    public static IEnumerable<FileDiff> Parse(string diffContent)
    {
        // Split by "diff --git"
        // But simpler: Iterate lines

        if (string.IsNullOrWhiteSpace(diffContent)) yield break;

        // Split by "diff --git "
        // But simpler: Iterate lines

        var chunks = Regex.Split(diffContent, @"^diff --git ", RegexOptions.Multiline)
                          .Where(s => !string.IsNullOrWhiteSpace(s));

        foreach (var chunk in chunks)
        {
            yield return ParseChunk(chunk);
        }
    }

    private static FileDiff ParseChunk(string chunk)
    {
        // Re-add "diff --git " if regex ate it? No, Split consumes it.
        // Chunk starts with "a/path b/path" presumably.

        var lines = chunk.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        // Line 0: "a/path b/path"
        // Parse path from b/path usually.
        var pathLine = lines[0];
        // Extract path. usually "a/path b/path". 
        // Or just scan for "+++ b/path" line later to be sure?
        // But for added/deleted, one might be /dev/null

        string path = "unknown";
        FileStatus status = FileStatus.Modified;

        // Check for new file / deleted file modes
        foreach (var line in lines)
        {
            if (line.StartsWith("new file mode")) status = FileStatus.Added;
            if (line.StartsWith("deleted file mode")) status = FileStatus.Deleted;

            if (line.StartsWith("+++ b/"))
            {
                path = line.Substring(6).Trim();
            }
            else if (line.StartsWith("+++ ") && status == FileStatus.Added)
            {
                // +++ b/new.file (standard)
                if (line.Length > 4) path = line.Substring(4).Trim();
            }
            else if (line.StartsWith("--- a/") && status == FileStatus.Deleted)
            {
                // For deleted, b/ is /dev/null usually. So take path from a/
                path = line.Substring(6).Trim();
            }
        }

        // Fallback for path if not found in +++ / ---
        if (path == "unknown")
        {
            // Try extracting from line 0: "a/src/File.cs b/src/File.cs"
            // Get everything after b/
            // Note: spaces in filenames make this hard, but for now assume no spaces or simple split
            var parts = pathLine.Split(' ');
            if (parts.Length >= 2)
            {
                var p = parts.Last();
                if (p.StartsWith("b/")) path = p.Substring(2);
                else path = p;
            }
        }

        // Collect content (hunks)
        // Usually starts after "+++ ..." line
        // We can just grab everything that starts with + or - or @
        // Or return the whole chunk as context?
        // Requirements say "Hunks text".
        var content = string.Join("\n", lines);

        return new FileDiff(path, status, content);
    }
}
