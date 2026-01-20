using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BinlogMcp.Tools;

/// <summary>
/// Item flow tracking tools - trace items through the build process.
/// </summary>
public static partial class BinlogTools
{
    [McpServerTool, Description("Traces an item through the build - shows which targets/tasks consumed, transformed, or output it")]
    public static string TraceItem(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Item type to trace (e.g., 'Compile', 'Reference', 'PackageReference')")] string itemType,
        [Description("Filter by item value/path pattern (optional, case-insensitive substring match)")] string? valuePattern = null,
        [Description("Filter to specific project (optional)")] string? projectFilter = null,
        [Description("Maximum number of items to trace (default: 20)")] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(itemType))
            return JsonSerializer.Serialize(new { error = "Item type is required" }, JsonOptions);

        return ExecuteBinlogTool(binlogPath, build =>
        {
            var addItems = new List<AddItem>();
            var removeItems = new List<RemoveItem>();
            var tasks = new List<MSBuildTask>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case AddItem a:
                        addItems.Add(a);
                        break;
                    case RemoveItem r:
                        removeItems.Add(r);
                        break;
                    case MSBuildTask t:
                        tasks.Add(t);
                        break;
                }
            });

            // Find all items of the specified type
            var matchingItems = addItems
                .Where(a => a.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                .SelectMany(a => a.Children.OfType<Item>().Select(item => new
                {
                    addItem = a,
                    item,
                    value = item.Text,
                    project = GetProjectName(a),
                    projectFile = GetProjectFile(a)
                }))
                .Where(x => x.project.MatchesFilter(projectFilter) && x.value.MatchesFilter(valuePattern))
                .DistinctBy(x => $"{x.project}:{x.value}")
                .Take(limit)
                .ToList();

            // For each matching item, trace its flow
            var itemTraces = matchingItems
                .Select(matchedItem =>
                {
                    var itemValue = matchedItem.value ?? "";
                    var itemFileName = Path.GetFileName(itemValue);
                    var events = new List<ItemFlowEvent>();

                    // 1. Find where it was added
                    var addEvent = new ItemFlowEvent
                    {
                        EventType = "Added",
                        Target = GetParentTargetName(matchedItem.addItem),
                        Project = matchedItem.project,
                        Details = $"Added to {itemType}",
                        Metadata = GetItemMetadataDict(matchedItem.item)
                    };
                    events.Add(addEvent);

                    // 2. Find tasks that consumed this item
                    foreach (var task in tasks)
                    {
                        var taskProject = GetProjectName(task);
                        if (taskProject.FailsFilter(projectFilter))
                            continue;

                        // Check task parameters for this item
                        foreach (var child in task.Children)
                        {
                            if (child is Parameter param)
                            {
                                var paramItems = param.Children.OfType<Item>().ToList();
                                var matchingParam = paramItems.Any(i =>
                                    i.Text?.Equals(itemValue, StringComparison.OrdinalIgnoreCase) == true ||
                                    i.Text?.EndsWith(itemFileName, StringComparison.OrdinalIgnoreCase) == true);

                                if (matchingParam)
                                {
                                    events.Add(new ItemFlowEvent
                                    {
                                        EventType = IsInputParameter(param.Name) ? "Consumed" : "Output",
                                        Target = GetParentTargetName(task),
                                        Task = task.Name,
                                        Project = taskProject,
                                        Details = $"Parameter: {param.Name}",
                                        DurationMs = GetDurationMs(task.StartTime, task.EndTime)
                                    });
                                }
                            }
                        }
                    }

                    // 3. Check if it was removed
                    var removed = removeItems
                        .Where(r => r.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(r => r.Children.OfType<Item>())
                        .Any(i => i.Text?.Equals(itemValue, StringComparison.OrdinalIgnoreCase) == true);

                    if (removed)
                    {
                        events.Add(new ItemFlowEvent
                        {
                            EventType = "Removed",
                            Details = $"Removed from {itemType}"
                        });
                    }

                    return new
                    {
                        itemType,
                        value = TruncateValue(itemValue, 150),
                        fileName = itemFileName,
                        project = matchedItem.project,
                        eventCount = events.Count,
                        wasConsumed = events.Any(e => e.EventType == "Consumed"),
                        wasOutput = events.Any(e => e.EventType == "Output"),
                        wasRemoved = removed,
                        metadata = GetItemMetadataDict(matchedItem.item),
                        flow = events.Select(e => new
                        {
                            eventType = e.EventType,
                            target = e.Target,
                            task = e.Task,
                            project = e.Project,
                            details = e.Details,
                            durationMs = e.DurationMs > 0 ? Math.Round(e.DurationMs, 1) : (double?)null
                        }).ToList()
                    };
                })
                .ToList();

            // Summary statistics
            var consumedItems = itemTraces.Count(t => t.wasConsumed);
            var outputItems = itemTraces.Count(t => t.wasOutput);
            var unusedItems = itemTraces.Count(t => !t.wasConsumed && !t.wasOutput);

            return new
            {
                file = binlogPath,
                itemType,
                valuePattern,
                projectFilter,
                summary = new
                {
                    totalItemsTraced = itemTraces.Count,
                    consumedByTasks = consumedItems,
                    outputByTasks = outputItems,
                    potentiallyUnused = unusedItems
                },
                itemTraces
            };
        });
    }

    private static bool IsInputParameter(string paramName)
    {
        var inputParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Sources", "SourceFiles", "InputFiles", "Files", "References", "Assemblies",
            "Content", "Resources", "EmbeddedResources", "Compile", "None",
            "ProjectReferences", "PackageReferences", "AnalyzerReferences"
        };
        return inputParams.Contains(paramName) ||
               paramName.EndsWith("Files", StringComparison.OrdinalIgnoreCase) ||
               paramName.EndsWith("Sources", StringComparison.OrdinalIgnoreCase) ||
               paramName.StartsWith("Input", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string>? GetItemMetadataDict(Item item)
    {
        var metadata = new Dictionary<string, string>();
        foreach (var child in item.Children.OfType<Metadata>())
        {
            metadata[child.Name] = TruncateValue(child.Value, 100) ?? "";
        }
        return metadata.Count > 0 ? metadata : null;
    }

    private class ItemFlowEvent
    {
        public required string EventType { get; set; }
        public string? Target { get; set; }
        public string? Task { get; set; }
        public string? Project { get; set; }
        public string? Details { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public double DurationMs { get; set; }
    }

    [McpServerTool, Description("Shows item transformations within targets - batching, metadata changes, and item modifications")]
    public static string GetItemTransforms(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter by item type (optional, e.g., 'Compile', 'Reference')")] string? itemType = null,
        [Description("Filter to specific project (optional)")] string? projectFilter = null,
        [Description("Filter to specific target (optional)")] string? targetFilter = null)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var addItems = new List<AddItem>();
            var removeItems = new List<RemoveItem>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case AddItem a:
                        addItems.Add(a);
                        break;
                    case RemoveItem r:
                        removeItems.Add(r);
                        break;
                }
            });

            // Group operations by target
            var targetOperations = new Dictionary<string, List<ItemOperation>>(StringComparer.OrdinalIgnoreCase);

            foreach (var addItem in addItems)
            {
                var projectName = GetProjectName(addItem) ?? "Unknown";
                var targetName = GetParentTargetName(addItem) ?? "Evaluation";

                if (projectName.FailsFilter(projectFilter))
                    continue;

                if (targetName.FailsFilter(targetFilter))
                    continue;

                if (!string.IsNullOrEmpty(itemType) &&
                    !addItem.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = $"{projectName}:{targetName}";
                if (!targetOperations.TryGetValue(key, out var ops))
                {
                    ops = [];
                    targetOperations[key] = ops;
                }

                var items = addItem.Children.OfType<Item>().ToList();
                ops.Add(new ItemOperation
                {
                    OperationType = "Add",
                    ItemType = addItem.Name,
                    ItemCount = items.Count,
                    SampleItems = items.Take(5).Select(i => TruncateValue(i.Text, 80)).ToList(),
                    HasMetadata = items.Any(i => i.Children.OfType<Metadata>().Any()),
                    MetadataNames = items
                        .SelectMany(i => i.Children.OfType<Metadata>().Select(m => m.Name))
                        .Distinct()
                        .Take(10)
                        .ToList()
                });
            }

            foreach (var removeItem in removeItems)
            {
                var projectName = GetProjectName(removeItem) ?? "Unknown";
                var targetName = GetParentTargetName(removeItem) ?? "Evaluation";

                if (projectName.FailsFilter(projectFilter))
                    continue;

                if (targetName.FailsFilter(targetFilter))
                    continue;

                if (!string.IsNullOrEmpty(itemType) &&
                    !removeItem.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = $"{projectName}:{targetName}";
                if (!targetOperations.TryGetValue(key, out var ops))
                {
                    ops = [];
                    targetOperations[key] = ops;
                }

                var items = removeItem.Children.OfType<Item>().ToList();
                ops.Add(new ItemOperation
                {
                    OperationType = "Remove",
                    ItemType = removeItem.Name,
                    ItemCount = items.Count,
                    SampleItems = items.Take(5).Select(i => TruncateValue(i.Text, 80)).ToList(),
                    HasMetadata = false,
                    MetadataNames = []
                });
            }

            // Detect transformations (same item type added and removed in same target)
            var transformations = targetOperations
                .Where(kvp => kvp.Value.Count > 1)
                .Select(kvp =>
                {
                    var parts = kvp.Key.Split(':');
                    var project = parts[0];
                    var target = parts.Length > 1 ? parts[1] : "Unknown";

                    var adds = kvp.Value.Where(o => o.OperationType == "Add").ToList();
                    var removes = kvp.Value.Where(o => o.OperationType == "Remove").ToList();

                    // Detect potential transforms (remove then add of same type)
                    var potentialTransforms = adds
                        .Where(a => removes.Any(r => r.ItemType == a.ItemType))
                        .Select(a => a.ItemType)
                        .Distinct()
                        .ToList();

                    return new
                    {
                        project,
                        target,
                        operationCount = kvp.Value.Count,
                        addCount = adds.Count,
                        removeCount = removes.Count,
                        itemTypesModified = kvp.Value.Select(o => o.ItemType).Distinct().ToList(),
                        potentialTransforms,
                        operations = kvp.Value.Select(o => new
                        {
                            operation = o.OperationType,
                            itemType = o.ItemType,
                            itemCount = o.ItemCount,
                            sampleItems = o.SampleItems,
                            hasMetadata = o.HasMetadata,
                            metadataNames = o.MetadataNames.Count > 0 ? o.MetadataNames : null
                        }).ToList()
                    };
                })
                .OrderByDescending(t => t.operationCount)
                .ToList();

            // Summary by item type
            var byItemType = targetOperations.Values
                .SelectMany(ops => ops)
                .GroupBy(o => o.ItemType)
                .Select(g => new
                {
                    itemType = g.Key,
                    totalOperations = g.Count(),
                    adds = g.Count(o => o.OperationType == "Add"),
                    removes = g.Count(o => o.OperationType == "Remove"),
                    totalItemsAffected = g.Sum(o => o.ItemCount)
                })
                .OrderByDescending(x => x.totalOperations)
                .Take(20)
                .ToList();

            return new
            {
                file = binlogPath,
                itemType,
                projectFilter,
                targetFilter,
                summary = new
                {
                    targetsWithOperations = targetOperations.Count,
                    totalAddOperations = targetOperations.Values.SelectMany(o => o).Count(o => o.OperationType == "Add"),
                    totalRemoveOperations = targetOperations.Values.SelectMany(o => o).Count(o => o.OperationType == "Remove"),
                    itemTypesModified = byItemType.Count,
                    targetsWithTransforms = transformations.Count(t => t.potentialTransforms.Count > 0)
                },
                byItemType,
                transformations = transformations.Take(30).ToList()
            };
        });
    }

    private class ItemOperation
    {
        public required string OperationType { get; set; }
        public required string ItemType { get; set; }
        public int ItemCount { get; set; }
        public required List<string?> SampleItems { get; set; }
        public bool HasMetadata { get; set; }
        public required List<string> MetadataNames { get; set; }
    }

    [McpServerTool, Description("Shows all MSBuild task invocations - which projects called which targets on which child projects")]
    public static string GetMSBuildTaskCalls(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter to specific calling project (optional)")] string? callerProjectFilter = null,
        [Description("Filter to specific target being called (optional)")] string? targetFilter = null)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var msbuildTasks = new List<(MSBuildTask task, string? callerProject, string? callerTarget)>();

            build.VisitAllChildren<MSBuildTask>(task =>
            {
                if (task.Name.Equals("MSBuild", StringComparison.OrdinalIgnoreCase) ||
                    task.Name.Equals("CallTarget", StringComparison.OrdinalIgnoreCase))
                {
                    var callerProject = GetProjectName(task);
                    var callerTarget = GetParentTargetName(task);
                    msbuildTasks.Add((task, callerProject, callerTarget));
                }
            });

            // Parse MSBuild task parameters to understand what's being called
            var calls = msbuildTasks
                .Select(t =>
                {
                    var task = t.task;

                    // Get Projects parameter
                    var projectsParam = task.Children.OfType<Parameter>()
                        .FirstOrDefault(p => p.Name.Equals("Projects", StringComparison.OrdinalIgnoreCase));
                    var targetProjects = projectsParam?.Children.OfType<Item>()
                        .Select(i => i.Text)
                        .ToList() ?? [];

                    // Get Targets parameter
                    var targetsParam = task.Children.OfType<Parameter>()
                        .FirstOrDefault(p => p.Name.Equals("Targets", StringComparison.OrdinalIgnoreCase));
                    var targetTargets = targetsParam?.Children.OfType<Item>()
                        .Select(i => i.Text)
                        .ToList() ?? [];

                    // Get Properties parameter
                    var propsParam = task.Children.OfType<Parameter>()
                        .FirstOrDefault(p => p.Name.Equals("Properties", StringComparison.OrdinalIgnoreCase));
                    var properties = propsParam?.Children.OfType<Item>()
                        .Select(i => TruncateValue(i.Text, 50))
                        .Take(5)
                        .ToList() ?? [];

                    return new
                    {
                        callerProject = t.callerProject,
                        callerTarget = t.callerTarget,
                        taskType = task.Name,
                        durationMs = GetDurationMs(task.StartTime, task.EndTime),
                        targetProjects = targetProjects.Select(p => Path.GetFileName(p)).ToList(),
                        targetProjectPaths = targetProjects,
                        targetsInvoked = targetTargets,
                        propertiesPassed = properties.Count > 0 ? properties : null
                    };
                })
                .Where(c => c.callerProject.MatchesFilter(callerProjectFilter) &&
                           (string.IsNullOrEmpty(targetFilter) || c.targetsInvoked.Any(t => t.MatchesFilter(targetFilter))))
                .OrderByDescending(c => c.durationMs)
                .ToList();

            // Build call graph
            var callGraph = calls
                .Where(c => c.targetProjects.Count > 0)
                .GroupBy(c => c.callerProject ?? "Unknown")
                .Select(g => new
                {
                    callerProject = g.Key,
                    callCount = g.Count(),
                    totalDurationMs = Math.Round(g.Sum(c => c.durationMs), 1),
                    targetProjects = g.SelectMany(c => c.targetProjects).Distinct().ToList(),
                    targetsInvoked = g.SelectMany(c => c.targetsInvoked).Where(t => t != null).Distinct().ToList()
                })
                .OrderByDescending(x => x.callCount)
                .ToList();

            // Find recursive/self calls
            var recursiveCalls = calls
                .Where(c => c.targetProjects.Any(p =>
                    Path.GetFileName(p)?.Equals(c.callerProject + ".csproj", StringComparison.OrdinalIgnoreCase) == true ||
                    Path.GetFileName(p)?.Equals(c.callerProject, StringComparison.OrdinalIgnoreCase) == true))
                .Select(c => new { c.callerProject, c.callerTarget, c.targetsInvoked })
                .ToList();

            // Summary
            var totalDuration = calls.Sum(c => c.durationMs);
            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            return new
            {
                file = binlogPath,
                callerProjectFilter,
                targetFilter,
                summary = new
                {
                    totalMSBuildCalls = calls.Count,
                    totalCallDurationMs = Math.Round(totalDuration, 1),
                    percentOfBuild = buildDuration > 0 ? Math.Round(totalDuration / buildDuration * 100, 1) : 0,
                    uniqueCallers = callGraph.Count,
                    recursiveCalls = recursiveCalls.Count
                },
                callGraph,
                recursiveCalls = recursiveCalls.Count > 0 ? recursiveCalls : null,
                calls = calls.Take(50).Select(c => new
                {
                    c.callerProject,
                    c.callerTarget,
                    c.taskType,
                    durationMs = Math.Round(c.durationMs, 1),
                    c.targetProjects,
                    c.targetsInvoked,
                    c.propertiesPassed
                }).ToList()
            };
        });
    }

    [McpServerTool, Description("Gets detailed item metadata for items of a specific type. Useful for debugging item transforms, understanding build inputs, and diagnosing metadata propagation.")]
    public static string GetItemMetadata(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Item type to analyze (e.g., 'Compile', 'Reference', 'PackageReference', 'ProjectReference')")] string itemType,
        [Description("Filter by item value/path pattern (optional)")] string? valueFilter = null,
        [Description("Filter to specific project (optional)")] string? projectFilter = null,
        [Description("Filter to specific metadata name (optional)")] string? metadataFilter = null,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Maximum items to return (default: 100)")] int limit = 100)
    {
        if (string.IsNullOrWhiteSpace(itemType))
            return JsonSerializer.Serialize(new { error = "Item type is required" }, JsonOptions);

        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, $"Item Metadata: {itemType}", build =>
        {
            var addItems = new List<AddItem>();
            build.VisitAllChildren<AddItem>(a => addItems.Add(a));

            // Find all items of the specified type
            var allItems = addItems
                .Where(a => a.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                .SelectMany(a => a.Children.OfType<Item>().Select(item => new
                {
                    item,
                    project = GetProjectName(a),
                    target = GetParentTargetName(a) ?? "Evaluation"
                }))
                .Where(x => x.project.MatchesFilter(projectFilter) && x.item.Text.MatchesFilter(valueFilter))
                .ToList();

            // Collect metadata statistics
            var metadataStats = new Dictionary<string, MetadataStats>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in allItems)
            {
                foreach (var meta in entry.item.Children.OfType<Metadata>())
                {
                    if (meta.Name.FailsFilter(metadataFilter)) continue;

                    if (!metadataStats.TryGetValue(meta.Name, out var stats))
                    {
                        stats = new MetadataStats { Name = meta.Name };
                        metadataStats[meta.Name] = stats;
                    }

                    stats.Count++;
                    stats.SampleValues.Add(meta.Value);
                }
            }

            // Build detailed item list
            var itemDetails = allItems
                .Take(limit)
                .Select(x =>
                {
                    var metadata = x.item.Children.OfType<Metadata>()
                        .Where(m => m.Name.MatchesFilter(metadataFilter))
                        .Select(m => new
                        {
                            name = m.Name,
                            value = TruncateValue(m.Value, 200)
                        })
                        .ToList();

                    return new
                    {
                        value = TruncateValue(x.item.Text, 150),
                        fileName = Path.GetFileName(x.item.Text ?? ""),
                        project = x.project,
                        target = x.target,
                        metadataCount = metadata.Count,
                        metadata
                    };
                })
                .ToList();

            // Summarize metadata names
            var metadataSummary = metadataStats.Values
                .OrderByDescending(s => s.Count)
                .Select(s => new
                {
                    name = s.Name,
                    count = s.Count,
                    uniqueValues = s.SampleValues.Distinct().Count(),
                    sampleValues = s.SampleValues.Distinct().Take(5).Select(v => TruncateValue(v, 50)).ToList()
                })
                .ToList();

            // Find items without metadata
            var itemsWithoutMetadata = allItems
                .Where(x => !x.item.Children.OfType<Metadata>().Any())
                .Take(10)
                .Select(x => TruncateValue(x.item.Text, 100))
                .ToList();

            // Identify common metadata patterns
            var patterns = new List<object>();

            // Check for version metadata (common in PackageReference)
            if (metadataStats.ContainsKey("Version"))
            {
                var versionCounts = allItems
                    .SelectMany(x => x.item.Children.OfType<Metadata>()
                        .Where(m => m.Name.Equals("Version", StringComparison.OrdinalIgnoreCase)))
                    .GroupBy(m => m.Value)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .Select(g => new { version = g.Key, count = g.Count() })
                    .ToList();

                patterns.Add(new { type = "Version Distribution", data = versionCounts });
            }

            // Check for HintPath (common in Reference)
            if (metadataStats.ContainsKey("HintPath"))
            {
                var hintPathRoots = allItems
                    .SelectMany(x => x.item.Children.OfType<Metadata>()
                        .Where(m => m.Name.Equals("HintPath", StringComparison.OrdinalIgnoreCase)))
                    .Select(m => GetPathRoot(m.Value))
                    .GroupBy(p => p)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => new { root = g.Key, count = g.Count() })
                    .ToList();

                patterns.Add(new { type = "HintPath Roots", data = hintPathRoots });
            }

            // Check for Private/CopyLocal metadata
            if (metadataStats.ContainsKey("Private") || metadataStats.ContainsKey("CopyLocal"))
            {
                var copyLocalCounts = allItems
                    .SelectMany(x => x.item.Children.OfType<Metadata>()
                        .Where(m => m.Name.Equals("Private", StringComparison.OrdinalIgnoreCase) ||
                                   m.Name.Equals("CopyLocal", StringComparison.OrdinalIgnoreCase)))
                    .GroupBy(m => $"{m.Name}={m.Value}")
                    .Select(g => new { setting = g.Key, count = g.Count() })
                    .ToList();

                patterns.Add(new { type = "CopyLocal/Private Settings", data = copyLocalCounts });
            }

            return new
            {
                file = binlogPath,
                itemType,
                valueFilter,
                projectFilter,
                metadataFilter,
                summary = new
                {
                    totalItems = allItems.Count,
                    itemsShown = itemDetails.Count,
                    uniqueMetadataNames = metadataStats.Count,
                    itemsWithMetadata = allItems.Count(x => x.item.Children.OfType<Metadata>().Any()),
                    itemsWithoutMetadata = allItems.Count(x => !x.item.Children.OfType<Metadata>().Any())
                },
                metadataSummary,
                patterns = patterns.Count > 0 ? patterns : null,
                items = itemDetails,
                sampleItemsWithoutMetadata = itemsWithoutMetadata.Count > 0 ? itemsWithoutMetadata : null
            };
        });
    }

    private static string GetPathRoot(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "Unknown";

        try
        {
            // Try to get first two path segments
            var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
                return $"{parts[0]}/{parts[1]}/...";
            return path;
        }
        catch
        {
            return "Unknown";
        }
    }

    private class MetadataStats
    {
        public required string Name { get; set; }
        public int Count { get; set; }
        public List<string?> SampleValues { get; set; } = [];
    }
}
