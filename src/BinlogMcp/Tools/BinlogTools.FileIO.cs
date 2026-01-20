using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BinlogMcp.Tools;

/// <summary>
/// File I/O analysis tools - duplicate writes, access patterns, caching opportunities.
/// </summary>
public static partial class BinlogTools
{
    [McpServerTool, Description("Detects files that are written multiple times during a build, which can indicate wasteful I/O or build misconfiguration")]
    public static string GetDuplicateFileWrites(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Minimum number of writes to consider as duplicate (default: 2)")] int minWrites = 2)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var fileWrites = new Dictionary<string, List<FileWriteInfo>>(StringComparer.OrdinalIgnoreCase);
            var messages = new List<Message>();
            var tasks = new List<MSBuildTask>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Message m:
                        messages.Add(m);
                        break;
                    case MSBuildTask t:
                        tasks.Add(t);
                        break;
                }
            });

            // Analyze Copy task outputs
            foreach (var task in tasks.Where(t => t.Name.Equals("Copy", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var child in task.Children)
                {
                    if (child is Folder folder && folder.Name.Equals("DestinationFiles", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var item in folder.Children.OfType<Item>())
                        {
                            RecordFileWrite(fileWrites, item.Text, task.Name, GetProjectName(task), task.StartTime);
                        }
                    }
                    else if (child is Parameter param && param.Name.Equals("DestinationFiles", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var item in param.Children.OfType<Item>())
                        {
                            RecordFileWrite(fileWrites, item.Text, task.Name, GetProjectName(task), task.StartTime);
                        }
                    }
                }
            }

            // Analyze other file-writing tasks
            var fileWritingTasks = new[] { "WriteLinesToFile", "Touch", "Move", "Csc", "Vbc", "Fsc" };
            foreach (var task in tasks.Where(t => fileWritingTasks.Contains(t.Name, StringComparer.OrdinalIgnoreCase)))
            {
                var outputValue = GetFirstParameterValue(task, "File", "OutputAssembly", "DestinationFile");
                if (!string.IsNullOrEmpty(outputValue))
                {
                    RecordFileWrite(fileWrites, outputValue, task.Name, GetProjectName(task), task.StartTime);
                }
            }

            // Look for "Copying file" messages as a fallback
            foreach (var msg in messages.Where(m => m.Text != null && m.Text.Contains("Copying file", StringComparison.OrdinalIgnoreCase)))
            {
                var text = msg.Text!;
                var toIndex = text.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
                if (toIndex > 0)
                {
                    var destPath = text.Substring(toIndex + 4).Trim().Trim('"', '.');
                    if (!string.IsNullOrEmpty(destPath))
                    {
                        RecordFileWrite(fileWrites, destPath, "Copy", GetProjectName(msg), msg.Timestamp);
                    }
                }
            }

            // Filter to duplicates and format output
            var duplicates = fileWrites
                .Where(kv => kv.Value.Count >= minWrites)
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv => new
                {
                    file = kv.Key,
                    fileName = Path.GetFileName(kv.Key),
                    writeCount = kv.Value.Count,
                    writes = kv.Value
                        .OrderBy(w => w.Timestamp)
                        .Select(w => new
                        {
                            task = w.TaskName,
                            project = w.ProjectName,
                            time = w.Timestamp.ToTimeString()
                        })
                        .ToList()
                })
                .ToList();

            // Categorize by type
            var byExtension = duplicates
                .GroupBy(d => Path.GetExtension(d.file)?.ToLowerInvariant() ?? "")
                .Select(g => new
                {
                    extension = string.IsNullOrEmpty(g.Key) ? "(no extension)" : g.Key,
                    count = g.Count(),
                    totalWrites = g.Sum(d => d.writeCount)
                })
                .OrderByDescending(x => x.totalWrites)
                .ToList();

            // Calculate waste
            var totalWrites = duplicates.Sum(d => d.writeCount);
            var uniqueFiles = duplicates.Count;
            var wastedWrites = totalWrites - uniqueFiles;

            return new
            {
                file = binlogPath,
                minWrites,
                summary = new
                {
                    filesWithDuplicateWrites = uniqueFiles,
                    totalWrites,
                    wastedWrites,
                    wastePercentage = totalWrites > 0 ? Math.Round((double)wastedWrites / totalWrites * 100, 1) : 0
                },
                byExtension,
                duplicates = duplicates.Take(50).ToList()
            };
        });
    }

    private static void RecordFileWrite(Dictionary<string, List<FileWriteInfo>> writes, string? filePath, string taskName, string? projectName, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        filePath = filePath.Replace('/', '\\').Trim();

        if (!writes.TryGetValue(filePath, out var list))
        {
            list = [];
            writes[filePath] = list;
        }

        list.Add(new FileWriteInfo
        {
            TaskName = taskName,
            ProjectName = projectName,
            Timestamp = timestamp
        });
    }

    private class FileWriteInfo
    {
        public required string TaskName { get; set; }
        public string? ProjectName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    [McpServerTool, Description("Analyzes file access patterns during build - identifies frequently read files, shared files across projects, and caching opportunities")]
    public static string GetFileAccessPatterns(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Minimum access count to include (default: 2)")] int minAccess = 2)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var messages = new List<Message>();
            var tasks = new List<MSBuildTask>();
            var addItems = new List<AddItem>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Message m:
                        messages.Add(m);
                        break;
                    case MSBuildTask t:
                        tasks.Add(t);
                        break;
                    case AddItem a:
                        addItems.Add(a);
                        break;
                }
            });

            // Track file accesses
            var fileAccesses = new Dictionary<string, FileAccessInfo>(StringComparer.OrdinalIgnoreCase);

            // Analyze Csc/Vbc/Fsc source file reads
            foreach (var task in tasks.Where(t => t.Name is "Csc" or "Vbc" or "Fsc"))
            {
                var sources = GetParameterValues(task, "Sources");
                var projectName = GetProjectName(task);
                foreach (var source in sources)
                {
                    if (!string.IsNullOrEmpty(source))
                        RecordFileAccess(fileAccesses, source, "Compile", task.Name, projectName);
                }

                var references = GetParameterValues(task, "References");
                foreach (var reference in references)
                {
                    if (!string.IsNullOrEmpty(reference))
                        RecordFileAccess(fileAccesses, reference, "Reference", task.Name, projectName);
                }
            }

            // Analyze Copy task source files
            foreach (var task in tasks.Where(t => t.Name.Equals("Copy", StringComparison.OrdinalIgnoreCase)))
            {
                var sourceFiles = GetParameterValues(task, "SourceFiles");
                var projectName = GetProjectName(task);
                foreach (var source in sourceFiles)
                {
                    if (!string.IsNullOrEmpty(source))
                        RecordFileAccess(fileAccesses, source, "Copy", task.Name, projectName);
                }
            }

            // Analyze ReadLinesFromFile task
            foreach (var task in tasks.Where(t => t.Name.Equals("ReadLinesFromFile", StringComparison.OrdinalIgnoreCase)))
            {
                var file = GetParameterValue(task, "File");
                var projectName = GetProjectName(task);
                if (!string.IsNullOrEmpty(file))
                    RecordFileAccess(fileAccesses, file, "Read", task.Name, projectName);
            }

            // Track Compile items from AddItem operations
            foreach (var addItem in addItems.Where(a => a.Name.Equals("Compile", StringComparison.OrdinalIgnoreCase)))
            {
                var projectName = GetProjectName(addItem);
                foreach (var item in addItem.Children.OfType<Item>())
                {
                    if (!string.IsNullOrEmpty(item.Text))
                        RecordFileAccess(fileAccesses, item.Text, "Compile", "MSBuild", projectName);
                }
            }

            // Track Reference items
            foreach (var addItem in addItems.Where(a => a.Name.Equals("Reference", StringComparison.OrdinalIgnoreCase)))
            {
                var projectName = GetProjectName(addItem);
                foreach (var item in addItem.Children.OfType<Item>())
                {
                    var hintPath = item.Children.OfType<Metadata>()
                        .FirstOrDefault(m => m.Name.Equals("HintPath", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (!string.IsNullOrEmpty(hintPath))
                        RecordFileAccess(fileAccesses, hintPath, "Reference", "MSBuild", projectName);
                }
            }

            // Look for file read messages
            foreach (var msg in messages.Where(m => m.Text != null))
            {
                var text = msg.Text!;
                if (text.Contains("Reading", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Loading", StringComparison.OrdinalIgnoreCase))
                {
                    var projectName = GetProjectName(msg);
                    if (TryExtractFilePath(text, out var filePath))
                        RecordFileAccess(fileAccesses, filePath, "Read", "Message", projectName);
                }
            }

            // Filter and analyze
            var frequentlyAccessed = fileAccesses.Values
                .Where(f => f.AccessCount >= minAccess)
                .OrderByDescending(f => f.AccessCount)
                .ToList();

            // Categorize by access type
            var byAccessType = frequentlyAccessed
                .SelectMany(f => f.AccessTypes.Select(at => new { File = f, Type = at }))
                .GroupBy(x => x.Type)
                .Select(g => new
                {
                    accessType = g.Key,
                    fileCount = g.Select(x => x.File.FilePath).Distinct().Count(),
                    totalAccesses = g.Sum(x => x.File.AccessCount)
                })
                .OrderByDescending(x => x.totalAccesses)
                .ToList();

            // Find shared files (accessed by multiple projects)
            var sharedFiles = frequentlyAccessed
                .Where(f => f.Projects.Count > 1)
                .OrderByDescending(f => f.Projects.Count)
                .ThenByDescending(f => f.AccessCount)
                .Take(30)
                .Select(f => new
                {
                    file = TruncateValue(f.FilePath, 100),
                    fileName = Path.GetFileName(f.FilePath),
                    accessCount = f.AccessCount,
                    projectCount = f.Projects.Count,
                    projects = f.Projects.Take(10).ToList(),
                    accessTypes = f.AccessTypes.ToList()
                })
                .ToList();

            // Identify caching opportunities (same file read multiple times)
            var cachingOpportunities = frequentlyAccessed
                .Where(f => f.AccessCount >= 3)
                .OrderByDescending(f => f.AccessCount)
                .Take(20)
                .Select(f => new
                {
                    file = TruncateValue(f.FilePath, 100),
                    fileName = Path.GetFileName(f.FilePath),
                    accessCount = f.AccessCount,
                    potentialSavings = f.AccessCount - 1,
                    accessedBy = f.Projects.Take(5).ToList(),
                    recommendation = GetCachingRecommendation(f)
                })
                .ToList();

            // Group by file extension
            var byExtension = frequentlyAccessed
                .GroupBy(f => Path.GetExtension(f.FilePath)?.ToLowerInvariant() ?? "unknown")
                .Select(g => new
                {
                    extension = g.Key,
                    fileCount = g.Count(),
                    totalAccesses = g.Sum(f => f.AccessCount),
                    avgAccessPerFile = Math.Round((double)g.Sum(f => f.AccessCount) / g.Count(), 1)
                })
                .OrderByDescending(x => x.totalAccesses)
                .Take(15)
                .ToList();

            // Hot files (most accessed)
            var hotFiles = frequentlyAccessed
                .Take(30)
                .Select(f => new
                {
                    file = TruncateValue(f.FilePath, 100),
                    fileName = Path.GetFileName(f.FilePath),
                    accessCount = f.AccessCount,
                    projectCount = f.Projects.Count,
                    accessTypes = f.AccessTypes.ToList()
                })
                .ToList();

            return new
            {
                file = binlogPath,
                minAccess,
                summary = new
                {
                    totalFilesTracked = fileAccesses.Count,
                    frequentlyAccessedFiles = frequentlyAccessed.Count,
                    sharedAcrossProjects = sharedFiles.Count,
                    cachingOpportunities = cachingOpportunities.Count,
                    totalAccesses = frequentlyAccessed.Sum(f => f.AccessCount)
                },
                byAccessType,
                byExtension,
                hotFiles,
                sharedFiles = sharedFiles.Count > 0 ? sharedFiles : null,
                cachingOpportunities = cachingOpportunities.Count > 0 ? cachingOpportunities : null
            };
        });
    }

    private static void RecordFileAccess(
        Dictionary<string, FileAccessInfo> accesses,
        string filePath,
        string accessType,
        string taskName,
        string? projectName)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        filePath = filePath.Replace('/', '\\').Trim();

        if (!accesses.TryGetValue(filePath, out var info))
        {
            info = new FileAccessInfo
            {
                FilePath = filePath,
                AccessCount = 0,
                AccessTypes = [],
                Tasks = [],
                Projects = []
            };
            accesses[filePath] = info;
        }

        info.AccessCount++;
        if (!info.AccessTypes.Contains(accessType))
            info.AccessTypes.Add(accessType);
        if (!info.Tasks.Contains(taskName))
            info.Tasks.Add(taskName);
        if (!string.IsNullOrEmpty(projectName) && !info.Projects.Contains(projectName))
            info.Projects.Add(projectName);
    }

    private static bool TryExtractFilePath(string text, out string filePath)
    {
        filePath = "";

        var patterns = new[]
        {
            @"(?:Reading|Loading|Opening)\s+(?:file\s+)?['""]?([A-Za-z]:\\[^'""]+|/[^'""]+)",
            @"from\s+['""]?([A-Za-z]:\\[^'""]+\.(?:dll|exe|cs|vb|fs|xml|json|config))",
            @"['""]([A-Za-z]:\\[^'""]+\.(?:dll|exe|cs|vb|fs|xml|json|config))['""]"
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                filePath = match.Groups[1].Value.Trim();
                return !string.IsNullOrEmpty(filePath);
            }
        }

        return false;
    }

    private static string GetCachingRecommendation(FileAccessInfo info)
    {
        var ext = Path.GetExtension(info.FilePath)?.ToLowerInvariant();

        if (ext is ".dll" or ".exe")
            return "Assembly loaded multiple times - consider assembly reference optimization";
        if (ext is ".cs" or ".vb" or ".fs")
            return "Source file compiled by multiple projects - consider shared project or common library";
        if (ext is ".xml" or ".json" or ".config")
            return "Config file read multiple times - consider build-time caching";
        if (info.Projects.Count > 1)
            return "File shared across projects - consider centralized location or Directory.Build.props";

        return "Frequently accessed file - review build configuration";
    }

    private class FileAccessInfo
    {
        public required string FilePath { get; set; }
        public int AccessCount { get; set; }
        public required List<string> AccessTypes { get; set; }
        public required List<string> Tasks { get; set; }
        public required List<string> Projects { get; set; }
    }
}
