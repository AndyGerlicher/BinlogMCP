using ModelContextProtocol.Server;
using System.ComponentModel;

namespace BinlogMcp.Tools;

public static partial class BinlogTools
{
    [McpServerTool, Description("Lists all embedded source files in the binlog's source archive. Binlogs can contain the actual project files (.csproj, .props, .targets, Directory.Build.props, etc.) that were used during the build. Use this to discover what files are available for inspection.")]
    public static string ListEmbeddedSourceFiles(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Optional filter pattern to match against file paths (case-insensitive substring match)")] string? filter = null)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var sourceFiles = build.SourceFiles;
            if (sourceFiles == null || sourceFiles.Count == 0)
            {
                return new
                {
                    message = "No embedded source files found in this binlog. The build may not have been recorded with embedded source files.",
                    fileCount = 0,
                    files = Array.Empty<object>()
                };
            }

            var files = sourceFiles
                .Where(f => string.IsNullOrEmpty(filter) || f.FullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(f => new
                {
                    path = f.FullPath,
                    sizeChars = f.Text?.Length ?? 0,
                    extension = Path.GetExtension(f.FullPath).ToLowerInvariant(),
                    fileName = Path.GetFileName(f.FullPath)
                })
                .ToList();

            var extensionSummary = files
                .GroupBy(f => f.extension)
                .OrderByDescending(g => g.Count())
                .Select(g => new { extension = g.Key, count = g.Count() })
                .ToList();

            return new
            {
                fileCount = files.Count,
                totalFiles = sourceFiles.Count,
                filtered = !string.IsNullOrEmpty(filter),
                extensionSummary,
                files = files.Select(f => new { f.path, f.sizeChars, f.extension }).ToList()
            };
        });
    }

    [McpServerTool, Description("Reads the content of a specific embedded source file from the binlog. Use ListEmbeddedSourceFiles first to discover available files. This is essential for root-cause analysis - lets you read the actual .csproj, .props, .targets, Directory.Build.props and other MSBuild files that were used during the build.")]
    public static string GetEmbeddedSourceFile(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Path of the embedded file to read (as returned by ListEmbeddedSourceFiles)")] string filePath)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var sourceFiles = build.SourceFiles;
            if (sourceFiles == null || sourceFiles.Count == 0)
            {
                return new { error = "No embedded source files found in this binlog." };
            }

            // Try exact match first
            var match = sourceFiles.FirstOrDefault(f => f.FullPath == filePath);

            // Try case-insensitive match
            match ??= sourceFiles.FirstOrDefault(f =>
                f.FullPath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return new
                {
                    path = match.FullPath,
                    sizeChars = match.Text?.Length ?? 0,
                    content = match.Text ?? ""
                };
            }

            // Try partial match (file name only)
            var fileName = Path.GetFileName(filePath);
            var partialMatches = sourceFiles
                .Where(f => Path.GetFileName(f.FullPath).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.FullPath)
                .ToList();

            if (partialMatches.Count == 1)
            {
                return new
                {
                    path = partialMatches[0].FullPath,
                    sizeChars = partialMatches[0].Text?.Length ?? 0,
                    content = partialMatches[0].Text ?? "",
                    note = $"Matched by file name. Full path: {partialMatches[0].FullPath}"
                };
            }

            if (partialMatches.Count > 1)
            {
                return new
                {
                    error = $"File not found: '{filePath}'. Multiple files match the name '{fileName}'.",
                    suggestions = partialMatches.Select(f => f.FullPath).Take(10).ToList()
                };
            }

            // Search for substring matches as suggestions
            var suggestions = sourceFiles
                .Where(f => f.FullPath.Contains(filePath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f.FullPath)
                .Select(f => f.FullPath)
                .Take(10)
                .ToList();

            return new
            {
                error = $"File not found: '{filePath}'",
                suggestions = suggestions.Count > 0 ? suggestions : null as List<string>,
                hint = suggestions.Count == 0 ? "Use ListEmbeddedSourceFiles to see available files." : null
            };
        });
    }
}
