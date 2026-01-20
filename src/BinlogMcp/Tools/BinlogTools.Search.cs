using BinlogMcp.Formatting;
using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BinlogMcp.Tools;

public static partial class BinlogTools
{
    [McpServerTool, Description("Searches binlog content for messages, errors, warnings, targets, tasks, or properties matching a query")]
    public static string SearchBinlog(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Search query (case-insensitive substring match)")] string query,
        [Description("Type to search: 'all', 'messages', 'errors', 'warnings', 'targets', 'tasks', 'properties' (default: all)")] string searchType = "all",
        [Description("Maximum results to return (default: 50)")] int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
            return JsonSerializer.Serialize(new { error = "Search query cannot be empty" }, JsonOptions);

        return ExecuteBinlogTool(binlogPath, build =>
        {
            var results = new SearchResults();
            var searchAll = string.Equals(searchType, "all", StringComparison.OrdinalIgnoreCase);

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Error e when (searchAll || searchType.Equals("errors", StringComparison.OrdinalIgnoreCase)):
                        if (MatchesQuery(e.Text, query) || MatchesQuery(e.Code, query))
                        {
                            results.Errors.Add(new SearchError
                            {
                                Message = e.Text,
                                Code = e.Code,
                                File = e.File,
                                LineNumber = e.LineNumber,
                                Project = e.ProjectFile
                            });
                        }
                        break;

                    case Warning w when (searchAll || searchType.Equals("warnings", StringComparison.OrdinalIgnoreCase)):
                        if (MatchesQuery(w.Text, query) || MatchesQuery(w.Code, query))
                        {
                            results.Warnings.Add(new SearchWarning
                            {
                                Message = w.Text,
                                Code = w.Code,
                                File = w.File,
                                LineNumber = w.LineNumber,
                                Project = w.ProjectFile
                            });
                        }
                        break;

                    case Message m when (searchAll || searchType.Equals("messages", StringComparison.OrdinalIgnoreCase)):
                        if (MatchesQuery(m.Text, query))
                        {
                            results.Messages.Add(new SearchMessage
                            {
                                Text = m.Text,
                                Project = GetProjectName(m)
                            });
                        }
                        break;

                    case Target t when (searchAll || searchType.Equals("targets", StringComparison.OrdinalIgnoreCase)):
                        if (MatchesQuery(t.Name, query))
                        {
                            results.Targets.Add(new SearchTarget
                            {
                                Name = t.Name,
                                Project = t.Project?.Name,
                                Succeeded = t.Succeeded,
                                DurationMs = GetDurationMs(t.StartTime, t.EndTime)
                            });
                        }
                        break;

                    case MSBuildTask task when (searchAll || searchType.Equals("tasks", StringComparison.OrdinalIgnoreCase)):
                        if (MatchesQuery(task.Name, query))
                        {
                            results.Tasks.Add(new SearchTask
                            {
                                Name = task.Name,
                                Project = GetProjectName(task),
                                ParentTarget = task.Parent is Target target ? target.Name : null,
                                DurationMs = GetDurationMs(task.StartTime, task.EndTime)
                            });
                        }
                        break;

                    case Property p when (searchAll || searchType.Equals("properties", StringComparison.OrdinalIgnoreCase)):
                        if (MatchesQuery(p.Name, query) || MatchesQuery(p.Value, query))
                        {
                            results.Properties.Add(new SearchProperty
                            {
                                Name = p.Name,
                                Value = p.Value,
                                Project = GetProjectName(p)
                            });
                        }
                        break;
                }
            });

            // Apply limits
            return new
            {
                file = binlogPath,
                query,
                searchType,
                totalMatches = results.TotalCount,
                errors = results.Errors.Take(limit).ToList(),
                errorCount = results.Errors.Count,
                warnings = results.Warnings.Take(limit).ToList(),
                warningCount = results.Warnings.Count,
                messages = results.Messages.Take(limit).ToList(),
                messageCount = results.Messages.Count,
                targets = results.Targets.Take(limit).ToList(),
                targetCount = results.Targets.Count,
                tasks = results.Tasks.Take(limit).ToList(),
                taskCount = results.Tasks.Count,
                properties = results.Properties.DistinctBy(p => $"{p.Name}={p.Value}").Take(limit).ToList(),
                propertyCount = results.Properties.DistinctBy(p => $"{p.Name}={p.Value}").Count()
            };
        });
    }

    private static bool MatchesQuery(string? text, string query)
    {
        return !string.IsNullOrEmpty(text) && text.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private class SearchResults
    {
        public List<SearchError> Errors { get; } = [];
        public List<SearchWarning> Warnings { get; } = [];
        public List<SearchMessage> Messages { get; } = [];
        public List<SearchTarget> Targets { get; } = [];
        public List<SearchTask> Tasks { get; } = [];
        public List<SearchProperty> Properties { get; } = [];

        public int TotalCount => Errors.Count + Warnings.Count + Messages.Count +
                                  Targets.Count + Tasks.Count + Properties.Count;
    }

    private class SearchError
    {
        public string? Message { get; set; }
        public string? Code { get; set; }
        public string? File { get; set; }
        public int LineNumber { get; set; }
        public string? Project { get; set; }
    }

    private class SearchWarning
    {
        public string? Message { get; set; }
        public string? Code { get; set; }
        public string? File { get; set; }
        public int LineNumber { get; set; }
        public string? Project { get; set; }
    }

    private class SearchMessage
    {
        public string? Text { get; set; }
        public string? Project { get; set; }
    }

    private class SearchTarget
    {
        public string? Name { get; set; }
        public string? Project { get; set; }
        public bool Succeeded { get; set; }
        public double DurationMs { get; set; }
    }

    private class SearchTask
    {
        public string? Name { get; set; }
        public string? Project { get; set; }
        public string? ParentTarget { get; set; }
        public double DurationMs { get; set; }
    }

    private class SearchProperty
    {
        public string? Name { get; set; }
        public string? Value { get; set; }
        public string? Project { get; set; }
    }

    [McpServerTool, Description("Gets MSBuild properties from a binlog file. Can filter by name or get all properties.")]
    public static string GetProperties(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Filter properties by name (case-insensitive substring match, optional)")] string? nameFilter = null,
        [Description("Include origin information (source file, context) for each property (default: false)")] bool includeOrigin = false,
        [Description("Maximum number of properties to return (default: 100)")] int limit = 100)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "MSBuild Properties", build =>
        {
            var properties = new List<Property>();
            build.VisitAllChildren<Property>(p => properties.Add(p));

            // Group by name to get unique property name-value pairs with project context
            var filteredProperties = properties
                .Where(p => p.Name.MatchesFilter(nameFilter))
                .ToList();

            object propertyData;
            if (includeOrigin)
            {
                propertyData = filteredProperties
                    .Select(p =>
                    {
                        var origin = GetPropertyOriginInfo(p);
                        return new
                        {
                            name = p.Name,
                            value = p.Value,
                            project = GetProjectName(p),
                            sourceFile = origin.sourceFile,
                            context = origin.context
                        };
                    })
                    .DistinctBy(p => $"{p.project}:{p.name}={p.value}:{p.sourceFile}")
                    .OrderBy(p => p.name)
                    .Take(limit)
                    .ToList();
            }
            else
            {
                propertyData = filteredProperties
                    .Select(p => new
                    {
                        name = p.Name,
                        value = p.Value,
                        project = GetProjectName(p)
                    })
                    .DistinctBy(p => $"{p.project}:{p.name}={p.value}")
                    .OrderBy(p => p.name)
                    .Take(limit)
                    .ToList();
            }

            // Common important properties to highlight
            var commonPropertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Configuration", "Platform", "TargetFramework", "TargetFrameworks",
                "OutputPath", "OutputType", "AssemblyName", "RootNamespace",
                "Version", "AssemblyVersion", "FileVersion", "PackageVersion",
                "MSBuildProjectDirectory", "MSBuildProjectFile"
            };

            var importantProperties = properties
                .Where(p => commonPropertyNames.Contains(p.Name))
                .Select(p => new { name = p.Name, value = p.Value })
                .DistinctBy(p => p.name)
                .OrderBy(p => p.name)
                .ToList();

            return new
            {
                file = binlogPath,
                nameFilter,
                includeOrigin,
                totalProperties = properties.DistinctBy(p => p.Name).Count(),
                returnedCount = ((System.Collections.ICollection)propertyData).Count,
                importantProperties,
                properties = propertyData
            };
        });
    }

    private static (string? sourceFile, string? context) GetPropertyOriginInfo(Property prop)
    {
        // Walk up the parent tree to find source information
        var current = prop.Parent;
        string? sourceFile = null;
        string? context = null;

        while (current != null)
        {
            switch (current)
            {
                case Target target:
                    sourceFile ??= target.SourceFilePath;
                    context = $"Target: {target.Name}";
                    break;
                case Project project:
                    sourceFile ??= project.ProjectFile;
                    context ??= $"Project: {project.Name}";
                    break;
                case ProjectEvaluation eval:
                    sourceFile ??= eval.ProjectFile;
                    context ??= "Project evaluation";
                    break;
                case Folder folder:
                    if (folder.Name.Equals("Properties", StringComparison.OrdinalIgnoreCase))
                        context ??= "Properties block";
                    else if (folder.Name.Equals("InitialProperties", StringComparison.OrdinalIgnoreCase))
                        context ??= "Initial properties (environment/global)";
                    break;
            }
            current = current.Parent;
        }

        return (sourceFile, context ?? "Unknown source");
    }

    [McpServerTool, Description("Gets MSBuild item groups from a binlog file (Compile, Reference, PackageReference, etc.)")]
    public static string GetItems(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Item type to retrieve (e.g., 'Compile', 'Reference', 'PackageReference'). If not specified, returns summary of all item types.")] string? itemType = null,
        [Description("Maximum number of items to return (default: 100)")] int limit = 100)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "MSBuild Items", build =>
        {
            var addItems = new List<AddItem>();
            build.VisitAllChildren<AddItem>(a => addItems.Add(a));

            if (string.IsNullOrEmpty(itemType))
            {
                // Return summary of all item types
                var itemTypeSummary = addItems
                    .GroupBy(a => a.Name)
                    .Select(g => new
                    {
                        itemType = g.Key,
                        count = g.Sum(a => a.Children.OfType<Item>().Count())
                    })
                    .OrderByDescending(x => x.count)
                    .ToList();

                return new
                {
                    file = binlogPath,
                    totalItemTypes = itemTypeSummary.Count,
                    itemTypes = itemTypeSummary
                };
            }

            // Get specific item type
            var items = addItems
                .Where(a => a.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                .SelectMany(a => a.Children.OfType<Item>().Select(item => new
                {
                    include = item.Text,
                    project = GetProjectFile(a),
                    metadata = GetItemMetadata(item)
                }))
                .DistinctBy(i => $"{i.project}:{i.include}")
                .Take(limit)
                .ToList();

            return new
            {
                file = binlogPath,
                itemType,
                totalItems = items.Count,
                items
            };
        });
    }

    private static Dictionary<string, string>? GetItemMetadata(Item item)
    {
        var metadata = new Dictionary<string, string>();
        foreach (var child in item.Children.OfType<Metadata>())
        {
            metadata[child.Name] = child.Value;
        }
        return metadata.Count > 0 ? metadata : null;
    }

    [McpServerTool, Description("Detects MSBuild properties that are set multiple times, which can indicate conflicts, unintended overrides, or inefficiency")]
    public static string GetPropertyReassignments(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter to specific property name (optional)")] string? propertyFilter = null,
        [Description("Minimum number of assignments to show (default: 2)")] int minAssignments = 2)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var propertyAssignments = new Dictionary<string, List<PropertyAssignmentInfo>>(StringComparer.OrdinalIgnoreCase);

            build.VisitAllChildren<Property>(prop =>
            {
                if (prop.Name.MatchesFilter(propertyFilter))
                {
                    var key = prop.Name;
                    if (!propertyAssignments.TryGetValue(key, out var list))
                    {
                        list = [];
                        propertyAssignments[key] = list;
                    }

                    list.Add(new PropertyAssignmentInfo
                    {
                        Value = prop.Value,
                        Project = GetProjectName(prop),
                        SourceFile = GetPropertySourceFile(prop)
                    });
                }
            });

            // Find properties with multiple assignments
            var reassignments = propertyAssignments
                .Where(kv => kv.Value.Count >= minAssignments)
                .OrderByDescending(kv => kv.Value.Count)
                .Select(kv =>
                {
                    var assignments = kv.Value;
                    var distinctValues = assignments.Select(a => a.Value).Distinct().ToList();
                    var hasConflict = distinctValues.Count > 1;

                    return new
                    {
                        property = kv.Key,
                        assignmentCount = assignments.Count,
                        distinctValueCount = distinctValues.Count,
                        hasConflict,
                        finalValue = assignments.LastOrDefault()?.Value,
                        assignments = assignments
                            .Select(a => new
                            {
                                value = TruncateValue(a.Value, 100),
                                project = a.Project,
                                sourceFile = a.SourceFile
                            })
                            .ToList()
                    };
                })
                .ToList();

            // Categorize
            var conflicts = reassignments.Where(r => r.hasConflict).ToList();
            var redundant = reassignments.Where(r => !r.hasConflict).ToList();

            // Common problematic properties to highlight
            var commonConflictProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Configuration", "Platform", "TargetFramework", "OutputPath",
                "IntermediateOutputPath", "BaseOutputPath", "BaseIntermediateOutputPath",
                "AssemblyName", "RootNamespace", "Version"
            };

            var importantConflicts = conflicts
                .Where(c => commonConflictProps.Contains(c.property))
                .ToList();

            return new
            {
                file = binlogPath,
                propertyFilter,
                minAssignments,
                summary = new
                {
                    totalReassignedProperties = reassignments.Count,
                    propertiesWithConflicts = conflicts.Count,
                    redundantAssignments = redundant.Count,
                    totalAssignments = reassignments.Sum(r => r.assignmentCount)
                },
                importantConflicts = importantConflicts.Count > 0 ? importantConflicts : null,
                conflicts = conflicts.Take(30).ToList(),
                redundantAssignments = redundant.Take(20).ToList()
            };
        });
    }

    private static string? GetPropertySourceFile(BaseNode node)
    {
        var current = node;
        while (current != null)
        {
            if (current is Project p && !string.IsNullOrEmpty(p.ProjectFile))
                return p.ProjectFile;
            if (current is Target t && !string.IsNullOrEmpty(t.SourceFilePath))
                return t.SourceFilePath;
            current = current.Parent;
        }
        return null;
    }

    private class PropertyAssignmentInfo
    {
        public string? Value { get; set; }
        public string? Project { get; set; }
        public string? SourceFile { get; set; }
    }

    [McpServerTool, Description("Traces a property's full evaluation history - shows initial value, each reassignment, and final value with source locations")]
    public static string TraceProperty(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Property name to trace (required)")] string propertyName,
        [Description("Filter to specific project (optional)")] string? projectFilter = null)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return JsonSerializer.Serialize(new { error = "Property name is required" }, JsonOptions);

        return ExecuteBinlogTool(binlogPath, build =>
        {
            var properties = new List<(Property prop, DateTime timestamp, int order)>();
            var order = 0;

            build.VisitAllChildren<Property>(p =>
            {
                if (p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    // Try to get timestamp from parent target/project
                    var timestamp = GetPropertyTimestamp(p);
                    properties.Add((p, timestamp, order++));
                }
            });

            // Group by project
            var byProject = properties
                .Select(p =>
                {
                    var projectName = GetProjectName(p.prop) ?? "Global";
                    var projectFile = GetProjectFile(p.prop);

                    if (projectName.FailsFilter(projectFilter))
                        return null;

                    var origin = GetPropertyOriginInfo(p.prop);

                    return new
                    {
                        projectName,
                        projectFile,
                        value = p.prop.Value,
                        order = p.order,
                        timestamp = p.timestamp,
                        sourceFile = origin.sourceFile,
                        context = origin.context
                    };
                })
                .Where(p => p != null)
                .GroupBy(p => p!.projectName)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p!.order).ToList());

            // Build trace per project
            var projectTraces = byProject
                .Select(kvp =>
                {
                    var assignments = kvp.Value;
                    var valueChanges = new List<object>();
                    string? previousValue = null;

                    for (int i = 0; i < assignments.Count; i++)
                    {
                        var a = assignments[i]!;
                        var isChange = previousValue != null && previousValue != a.value;
                        var isInitial = i == 0;
                        var isFinal = i == assignments.Count - 1;

                        valueChanges.Add(new
                        {
                            step = i + 1,
                            value = TruncateValue(a.value, 200),
                            previousValue = isChange ? TruncateValue(previousValue, 100) : null,
                            sourceFile = a.sourceFile != null ? Path.GetFileName(a.sourceFile) : null,
                            fullSourcePath = a.sourceFile,
                            context = a.context,
                            isInitial,
                            isFinal,
                            isChange
                        });

                        previousValue = a.value;
                    }

                    var distinctValues = assignments.Select(a => a!.value).Distinct().ToList();

                    return new
                    {
                        project = kvp.Key,
                        projectFile = assignments.FirstOrDefault()?.projectFile,
                        totalAssignments = assignments.Count,
                        valueChanges = distinctValues.Count - 1,
                        initialValue = assignments.FirstOrDefault()?.value,
                        finalValue = assignments.LastOrDefault()?.value,
                        hasConflict = distinctValues.Count > 1,
                        trace = valueChanges
                    };
                })
                .ToList();

            // Summary across all projects
            var allValues = properties.Select(p => p.prop.Value).Distinct().ToList();
            var allFinalValues = projectTraces
                .Select(p => p.finalValue)
                .Distinct()
                .ToList();

            return new
            {
                file = binlogPath,
                propertyName,
                projectFilter,
                summary = new
                {
                    totalAssignments = properties.Count,
                    projectsWithProperty = projectTraces.Count,
                    distinctValues = allValues.Count,
                    hasInconsistentFinalValues = allFinalValues.Count > 1,
                    allDistinctValues = allValues.Take(10).Select(v => TruncateValue(v, 100)).ToList()
                },
                projectTraces
            };
        });
    }

    private static DateTime GetPropertyTimestamp(Property prop)
    {
        var current = prop.Parent;
        while (current != null)
        {
            switch (current)
            {
                case Target t:
                    return t.StartTime;
                case Project p:
                    return p.StartTime;
                case ProjectEvaluation pe:
                    return pe.StartTime;
            }
            current = current.Parent;
        }
        return DateTime.MinValue;
    }
}
