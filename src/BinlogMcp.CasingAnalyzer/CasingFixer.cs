using System.Text;
using System.Text.RegularExpressions;

namespace BinlogMcp.CasingAnalyzer;

/// <summary>
/// Fixes casing in MSBuild source files (.props, .targets, *proj).
/// Only changes path segment casing to match the canonical (disk) casing.
/// </summary>
public class CasingFixer
{
    private readonly CanonicalPathResolver _resolver;
    private readonly List<FixResult> _results = [];

    public IReadOnlyList<FixResult> Results => _results;
    public int FilesModified => _results.Count(r => r.Modified);
    public int TotalReplacements => _results.Sum(r => r.ReplacementCount);

    public CasingFixer(CanonicalPathResolver resolver)
    {
        _resolver = resolver;
    }

    /// <summary>
    /// Finds and fixes all MSBuild source files in the directory.
    /// </summary>
    public async Task<IReadOnlyList<FixResult>> FixDirectoryAsync(
        string directoryPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Find all source files
        var sourceFiles = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Where(f => IsSourceFile(f))
            .ToList();

        progress?.Report($"Found {sourceFiles.Count} source files to check");

        foreach (var file in sourceFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await FixFileAsync(file);
                _results.Add(result);

                if (result.Modified)
                {
                    progress?.Report($"  Fixed: {GetRelativePath(directoryPath, file)} ({result.ReplacementCount} changes)");
                }
            }
            catch (Exception ex)
            {
                _results.Add(new FixResult(file, false, 0, ex.Message));
            }
        }

        return _results;
    }

    /// <summary>
    /// Fixes casing in a single file.
    /// </summary>
    public async Task<FixResult> FixFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return new FixResult(filePath, false, 0, "File not found");

        // Read with encoding detection to preserve BOM
        string content;
        Encoding encoding;
        using (var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true))
        {
            content = await reader.ReadToEndAsync();
            encoding = reader.CurrentEncoding;
        }
        var originalContent = content;
        var replacementCount = 0;

        var fileDir = Path.GetDirectoryName(filePath) ?? filePath;

        // Match path-like patterns: sequences of segments separated by \ or /
        // We look for quoted paths or paths in XML attributes
        var pathPattern = @"(?<prefix>[""'>])(?<path>(?:[^""'<>\r\n]*[\\\/])+[^""'<>\r\n]*)(?<suffix>[""'<])";
        var pathRegex = new Regex(pathPattern, RegexOptions.Compiled);

        content = pathRegex.Replace(content, pathMatch =>
        {
            var prefix = pathMatch.Groups["prefix"].Value;
            var path = pathMatch.Groups["path"].Value;
            var suffix = pathMatch.Groups["suffix"].Value;

            var fixedPath = FixPathCasing(path, fileDir, ref replacementCount);
            return prefix + fixedPath + suffix;
        });

        if (content != originalContent)
        {
            // Write with same encoding to preserve BOM
            await File.WriteAllTextAsync(filePath, content, encoding);
            return new FixResult(filePath, true, replacementCount, null);
        }

        return new FixResult(filePath, false, 0, null);
    }

    /// <summary>
    /// Fixes casing in a path string by verifying each segment against disk.
    /// </summary>
    private string FixPathCasing(string path, string fileDir, ref int replacementCount)
    {
        // Split into segments, preserving separators
        var segmentPattern = @"([^\\\/]+)|([\\\/]+)";
        var regex = new Regex(segmentPattern);
        var matches = regex.Matches(path);

        var result = new List<string>();
        var currentDir = fileDir;
        var canResolve = true;

        foreach (Match match in matches)
        {
            var part = match.Value;

            // If it's a separator, keep it
            if (part.All(c => c == '\\' || c == '/'))
            {
                result.Add(part);
                continue;
            }

            // Skip MSBuild expressions - they break path resolution
            if (part.StartsWith('$') || part.StartsWith('@') || part.StartsWith('%') || part.Contains('$'))
            {
                result.Add(part);
                canResolve = false; // Can't resolve paths after MSBuild properties
                continue;
            }

            // Handle relative path markers
            if (part == ".")
            {
                result.Add(part);
                continue;
            }
            if (part == "..")
            {
                result.Add(part);
                if (canResolve && currentDir != null)
                {
                    currentDir = Path.GetDirectoryName(currentDir);
                }
                continue;
            }

            // Skip version numbers
            if (part.All(c => char.IsDigit(c) || c == '.'))
            {
                result.Add(part);
                continue;
            }

            // Try to get canonical casing from disk at this specific location
            if (canResolve && currentDir != null)
            {
                var canonical = _resolver.GetCanonicalSegmentFromDisk(part, currentDir);
                if (canonical != null)
                {
                    if (!string.Equals(part, canonical, StringComparison.Ordinal))
                    {
                        replacementCount++;
                        result.Add(canonical);
                    }
                    else
                    {
                        result.Add(part);
                    }

                    // Update current directory for next segment
                    var nextDir = Path.Combine(currentDir, canonical);
                    if (Directory.Exists(nextDir))
                    {
                        currentDir = nextDir;
                    }
                    else
                    {
                        // It's a file, can't go deeper
                        canResolve = false;
                    }
                    continue;
                }
            }

            // Couldn't verify on disk - keep original
            result.Add(part);
            canResolve = false;
        }

        return string.Join("", result);
    }

    private static bool IsSourceFile(string path)
    {
        // Skip files in common non-source directories
        var lowerPath = path.ToLowerInvariant();
        if (lowerPath.Contains("\\bin\\") ||
            lowerPath.Contains("\\obj\\") ||
            lowerPath.Contains("\\.git\\") ||
            lowerPath.Contains("\\.vs\\") ||
            lowerPath.Contains("\\node_modules\\"))
            return false;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".props" or ".targets" or ".csproj" or ".vbproj" or ".fsproj" or ".proj" or ".vcxproj";
    }

    private static string GetRelativePath(string basePath, string fullPath)
    {
        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
        {
            var relative = fullPath[basePath.Length..];
            return relative.TrimStart('\\', '/');
        }
        return fullPath;
    }
}

/// <summary>
/// Result of fixing a single file.
/// </summary>
public record FixResult(
    string FilePath,
    bool Modified,
    int ReplacementCount,
    string? Error
);
