using BinlogMcp.Formatting;
using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace BinlogMcp.Tools;

[McpServerToolType]
public static partial class BinlogTools
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static readonly HashSet<string> IoTaskNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Copy", "Move", "Delete", "Touch", "MakeDir", "RemoveDir",
        "WriteLinesToFile", "ReadLinesFromFile", "Exec", "DownloadFile"
    };

    [McpServerTool, Description("Gets binlog cache statistics and optionally clears the cache")]
    public static string GetCacheStats(
        [Description("Set to true to clear the cache after returning stats")] bool clearCache = false)
    {
        var stats = BinlogCache.GetStats();

        if (clearCache)
        {
            BinlogCache.Clear();
        }

        return JsonSerializer.Serialize(new
        {
            enabled = stats.IsEnabled,
            cachedCount = stats.CachedCount,
            maxSize = stats.MaxSize,
            estimatedMemoryMB = Math.Round(stats.EstimatedMemoryMB, 2),
            cacheCleared = clearCache,
            cachedFiles = stats.CachedFiles.Select(f => new
            {
                path = f.Path,
                lastModified = f.LastModified.ToDateTimeString(),
                lastAccessed = f.LastAccessed.ToDateTimeString()
            }).ToList()
        }, JsonOptions);
    }

    [McpServerTool, Description("Lists all binlog files in a directory")]
    public static string ListBinlogs(
        [Description("Directory path to search for binlog files")] string directoryPath,
        [Description("Search subdirectories recursively (default: false)")] bool recursive = false)
    {
        var validationError = ValidateDirectoryExists(directoryPath);
        if (validationError != null)
            return validationError;

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var binlogFiles = Directory.GetFiles(directoryPath, "*.binlog", searchOption);

        var results = binlogFiles.Select(filePath =>
        {
            var fileInfo = new FileInfo(filePath);
            return new
            {
                path = filePath,
                fileName = fileInfo.Name,
                sizeBytes = fileInfo.Length,
                sizeFormatted = FormatFileSize(fileInfo.Length),
                lastModified = fileInfo.LastWriteTime.ToDateTimeString()
            };
        })
        .OrderByDescending(f => f.lastModified)
        .ToList();

        return JsonSerializer.Serialize(new
        {
            directory = directoryPath,
            recursive,
            count = results.Count,
            files = results
        }, JsonOptions);
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    [McpServerTool, Description("Gets a summary of a binlog file including build result, duration, errors and warnings count")]
    public static string GetBuildSummary(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Build Summary", build =>
        {
            var errors = new List<Error>();
            var warnings = new List<Warning>();
            var projects = new List<Project>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Error e:
                        errors.Add(e);
                        break;
                    case Warning w:
                        warnings.Add(w);
                        break;
                    case Project p:
                        projects.Add(p);
                        break;
                }
            });

            var duration = GetDuration(build.StartTime, build.EndTime);

            return new
            {
                file = binlogPath,
                succeeded = build.Succeeded,
                startTime = build.StartTime.ToDateTimeString(),
                endTime = build.EndTime.ToDateTimeString(),
                duration = new
                {
                    totalSeconds = duration.TotalSeconds,
                    formatted = FormatDuration(duration)
                },
                errorCount = errors.Count,
                warningCount = warnings.Count,
                projectCount = projects.DistinctBy(p => p.Name).Count(),
                projects = projects
                    .DistinctBy(p => p.Name)
                    .Select(p => new
                    {
                        name = p.Name,
                        targetFramework = p.TargetFramework
                    }).ToList()
            };
        });
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
        if (duration.TotalMinutes >= 1)
            return $"{duration.Minutes}m {duration.Seconds}s";
        return $"{duration.TotalSeconds:0.##}s";
    }

    [McpServerTool, Description("Gets all errors from a binlog file")]
    public static string GetErrors(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Build Errors", build =>
        {
            var errors = new List<Error>();
            build.VisitAllChildren<Error>(e => errors.Add(e));

            return new
            {
                file = binlogPath,
                count = errors.Count,
                errors = errors.Select(e => new
                {
                    message = e.Text,
                    code = e.Code,
                    file = e.File,
                    lineNumber = e.LineNumber,
                    columnNumber = e.ColumnNumber,
                    projectFile = e.ProjectFile
                }).ToList()
            };
        });
    }

    [McpServerTool, Description("Gets all warnings from a binlog file")]
    public static string GetWarnings(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Build Warnings", build =>
        {
            var warnings = new List<Warning>();
            build.VisitAllChildren<Warning>(w => warnings.Add(w));

            return new
            {
                file = binlogPath,
                count = warnings.Count,
                warnings = warnings.Select(w => new
                {
                    message = w.Text,
                    code = w.Code,
                    file = w.File,
                    lineNumber = w.LineNumber,
                    columnNumber = w.ColumnNumber,
                    projectFile = w.ProjectFile
                }).ToList()
            };
        });
    }

    // Shared helper methods used by multiple partial classes

    #region Validation Helpers

    /// <summary>
    /// Validates that a file exists. Returns error JSON if not found, null if valid.
    /// </summary>
    internal static string? ValidateFileExists(string path, string? fileDescription = null)
    {
        if (!File.Exists(path))
        {
            var desc = fileDescription != null ? $"{fileDescription} file" : "File";
            return JsonSerializer.Serialize(new { error = $"{desc} not found: {path}" }, JsonOptions);
        }
        return null;
    }

    /// <summary>
    /// Validates that a directory exists. Returns error JSON if not found, null if valid.
    /// </summary>
    internal static string? ValidateDirectoryExists(string path)
    {
        if (!Directory.Exists(path))
            return JsonSerializer.Serialize(new { error = $"Directory not found: {path}" }, JsonOptions);
        return null;
    }

    #endregion

    #region Binlog Execution Helpers

    /// <summary>
    /// Executes a tool that operates on a single binlog file with standard error handling.
    /// Uses the BinlogCache for improved performance on repeated reads.
    /// </summary>
    internal static string ExecuteBinlogTool(string binlogPath, Func<Build, object> toolLogic)
    {
        var validationError = ValidateFileExists(binlogPath);
        if (validationError != null)
            return validationError;

        try
        {
            var build = BinlogCache.GetOrLoad(binlogPath);
            var result = toolLogic(build);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to read binlog: {ex.Message}" }, JsonOptions);
        }
    }

    /// <summary>
    /// Executes a tool that operates on a single binlog file with format support.
    /// Uses the BinlogCache for improved performance on repeated reads.
    /// </summary>
    internal static string ExecuteBinlogToolWithFormat(
        string binlogPath,
        OutputFormat format,
        string title,
        Func<Build, object> toolLogic)
    {
        var validationError = ValidateFileExists(binlogPath);
        if (validationError != null)
            return validationError;

        try
        {
            var build = BinlogCache.GetOrLoad(binlogPath);
            var result = toolLogic(build);
            return OutputFormatter.Format(result, format, title);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to read binlog: {ex.Message}" }, JsonOptions);
        }
    }

    /// <summary>
    /// Executes a tool that compares two binlog files with standard error handling.
    /// Uses the BinlogCache for improved performance on repeated reads.
    /// </summary>
    internal static string ExecuteBinlogComparison(
        string baselinePath,
        string comparisonPath,
        Func<Build, Build, object> toolLogic)
    {
        var baselineError = ValidateFileExists(baselinePath, "Baseline");
        if (baselineError != null)
            return baselineError;

        var comparisonError = ValidateFileExists(comparisonPath, "Comparison");
        if (comparisonError != null)
            return comparisonError;

        try
        {
            var baselineBuild = BinlogCache.GetOrLoad(baselinePath);
            var comparisonBuild = BinlogCache.GetOrLoad(comparisonPath);
            var result = toolLogic(baselineBuild, comparisonBuild);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to compare binlogs: {ex.Message}" }, JsonOptions);
        }
    }

    /// <summary>
    /// Executes a tool that compares two binlog files with format support.
    /// Uses the BinlogCache for improved performance on repeated reads.
    /// </summary>
    internal static string ExecuteBinlogComparisonWithFormat(
        string baselinePath,
        string comparisonPath,
        OutputFormat format,
        string title,
        Func<Build, Build, object> toolLogic)
    {
        var baselineError = ValidateFileExists(baselinePath, "Baseline");
        if (baselineError != null)
            return baselineError;

        var comparisonError = ValidateFileExists(comparisonPath, "Comparison");
        if (comparisonError != null)
            return comparisonError;

        try
        {
            var baselineBuild = BinlogCache.GetOrLoad(baselinePath);
            var comparisonBuild = BinlogCache.GetOrLoad(comparisonPath);
            var result = toolLogic(baselineBuild, comparisonBuild);
            return OutputFormatter.Format(result, format, title);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to compare binlogs: {ex.Message}" }, JsonOptions);
        }
    }

    #endregion

    #region Format Helpers

    /// <summary>
    /// Tries to parse a format string and returns an error JSON if invalid.
    /// </summary>
    internal static string? TryParseFormatWithError(string? format, out OutputFormat outputFormat)
    {
        if (OutputFormatter.TryParseFormat(format, out outputFormat))
            return null;

        return JsonSerializer.Serialize(new { error = $"Invalid format: {format}. Valid formats: json, markdown, csv, timeline" }, JsonOptions);
    }

    #endregion

    #region Duration Helpers

    /// <summary>
    /// Calculates duration in milliseconds from start and end times.
    /// </summary>
    internal static double GetDurationMs(DateTime startTime, DateTime endTime)
        => (endTime - startTime).TotalMilliseconds;

    /// <summary>
    /// Calculates duration as TimeSpan from start and end times.
    /// </summary>
    internal static TimeSpan GetDuration(DateTime startTime, DateTime endTime)
        => endTime - startTime;

    #endregion

    #region Parameter Extraction Helpers

    /// <summary>
    /// Gets the first value of a named parameter from a node's children.
    /// Parameter values are stored in child Item nodes, not on the Parameter itself.
    /// </summary>
    internal static string? GetParameterValue(TreeNode node, string parameterName)
    {
        foreach (var child in node.Children)
        {
            if (child is Parameter param &&
                param.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return param.Children.OfType<Item>().FirstOrDefault()?.Text;
            }
        }
        return null;
    }

    /// <summary>
    /// Gets all values of a named parameter from a node's children.
    /// </summary>
    internal static List<string> GetParameterValues(TreeNode node, string parameterName)
    {
        foreach (var child in node.Children)
        {
            if (child is Parameter param &&
                param.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return param.Children.OfType<Item>().Select(i => i.Text).ToList();
            }
        }
        return [];
    }

    /// <summary>
    /// Gets the first matching parameter value from a list of parameter names.
    /// </summary>
    internal static string? GetFirstParameterValue(TreeNode node, params string[] parameterNames)
    {
        foreach (var child in node.Children)
        {
            if (child is Parameter param &&
                parameterNames.Any(n => param.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            {
                return param.Children.OfType<Item>().FirstOrDefault()?.Text;
            }
        }
        return null;
    }

    #endregion

    #region String Helpers

    /// <summary>
    /// Truncates a string to a maximum length, appending "..." if truncated.
    /// </summary>
    internal static string? TruncateValue(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    #endregion

    #region Project Resolution Helpers

    internal static string? GetProjectName(BaseNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is Project p)
                return p.Name;
            current = current.Parent;
        }
        return null;
    }

    internal static string? GetProjectFile(BaseNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is Project p)
                return p.ProjectFile;
            current = current.Parent;
        }
        return null;
    }

    internal static string? GetParentTargetName(BaseNode node)
    {
        var current = node.Parent;
        while (current != null)
        {
            if (current is Target t)
                return t.Name;
            current = current.Parent;
        }
        return null;
    }

    #endregion
}
