using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace BinlogMcp.Tools;

/// <summary>
/// Debugging-focused tools for understanding MSBuild behavior.
/// Based on common debugging scenarios from https://dfederm.com/debugging-msbuild/
/// </summary>
public static partial class BinlogTools
{
    [McpServerTool, Description("Analyzes why targets were executed - shows DependsOnTargets, BeforeTargets, AfterTargets relationships and what triggered each target")]
    public static string GetTargetExecutionReasons(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter to specific target name (optional)")] string? targetFilter = null,
        [Description("Filter to specific project name (optional)")] string? projectFilter = null,
        [Description("Maximum number of targets to return (default: 50)")] int limit = 50)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var targets = new List<Target>();
            var messages = new List<Message>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Target t:
                        targets.Add(t);
                        break;
                    case Message m:
                        messages.Add(m);
                        break;
                }
            });

            // Build a map of target info including execution reasons
            var targetInfoMap = new Dictionary<string, TargetExecutionInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                var projectName = target.Project?.Name ?? "Unknown";
                var key = $"{projectName}:{target.Name}";

                // Apply filters
                if (target.Name.FailsFilter(targetFilter))
                    continue;

                if (projectName.FailsFilter(projectFilter))
                    continue;

                if (!targetInfoMap.TryGetValue(key, out var info))
                {
                    // Parse DependsOnTargets
                    var dependsOn = ParseTargetList(target.DependsOnTargets);

                    info = new TargetExecutionInfo
                    {
                        Name = target.Name,
                        Project = projectName,
                        ProjectFile = target.Project?.ProjectFile,
                        DependsOnTargets = dependsOn,
                        TriggeredBy = [],
                        BeforeTargets = [],
                        AfterTargets = [],
                        SourceFile = target.SourceFilePath,
                        DurationMs = GetDurationMs(target.StartTime, target.EndTime),
                        Succeeded = target.Succeeded,
                        WasSkipped = false,
                        ExecutionReasons = []
                    };
                    targetInfoMap[key] = info;
                }
            }

            // Analyze messages to find BeforeTargets/AfterTargets relationships
            // MSBuild logs messages like "Target X depends on target Y"
            foreach (var msg in messages)
            {
                if (string.IsNullOrEmpty(msg.Text))
                    continue;

                var text = msg.Text;

                // Look for "Building target X because BeforeTargets='Y'" patterns
                if (text.Contains("BeforeTargets", StringComparison.OrdinalIgnoreCase))
                {
                    var projectName = GetProjectName(msg) ?? "Unknown";
                    // Try to extract target relationships from the message
                    var parts = text.ExtractBeforeAfterTargets("BeforeTargets");
                    if (parts.sourceTarget != null && parts.triggerTarget != null)
                    {
                        var key = $"{projectName}:{parts.sourceTarget}";
                        if (targetInfoMap.TryGetValue(key, out var info))
                        {
                            if (!info.BeforeTargets.Contains(parts.triggerTarget))
                                info.BeforeTargets.Add(parts.triggerTarget);
                            if (!info.ExecutionReasons.Contains($"BeforeTargets='{parts.triggerTarget}'"))
                                info.ExecutionReasons.Add($"BeforeTargets='{parts.triggerTarget}'");
                        }
                    }
                }

                if (text.Contains("AfterTargets", StringComparison.OrdinalIgnoreCase))
                {
                    var projectName = GetProjectName(msg) ?? "Unknown";
                    var parts = text.ExtractBeforeAfterTargets("AfterTargets");
                    if (parts.sourceTarget != null && parts.triggerTarget != null)
                    {
                        var key = $"{projectName}:{parts.sourceTarget}";
                        if (targetInfoMap.TryGetValue(key, out var info))
                        {
                            if (!info.AfterTargets.Contains(parts.triggerTarget))
                                info.AfterTargets.Add(parts.triggerTarget);
                            if (!info.ExecutionReasons.Contains($"AfterTargets='{parts.triggerTarget}'"))
                                info.ExecutionReasons.Add($"AfterTargets='{parts.triggerTarget}'");
                        }
                    }
                }
            }

            // Build reverse dependency map (who triggered this target via DependsOnTargets)
            foreach (var kvp in targetInfoMap)
            {
                foreach (var dep in kvp.Value.DependsOnTargets)
                {
                    var depKey = $"{kvp.Value.Project}:{dep}";
                    if (targetInfoMap.TryGetValue(depKey, out var depInfo))
                    {
                        if (!depInfo.TriggeredBy.Contains(kvp.Value.Name))
                            depInfo.TriggeredBy.Add(kvp.Value.Name);
                    }
                }
            }

            // Determine execution reasons for each target
            foreach (var info in targetInfoMap.Values)
            {
                if (info.DependsOnTargets.Count > 0)
                {
                    info.ExecutionReasons.Add($"DependsOnTargets: {string.Join(", ", info.DependsOnTargets)}");
                }
                if (info.TriggeredBy.Count > 0)
                {
                    info.ExecutionReasons.Add($"Required by: {string.Join(", ", info.TriggeredBy)}");
                }
                if (info.ExecutionReasons.Count == 0)
                {
                    info.ExecutionReasons.Add("Entry point target (explicitly invoked)");
                }
            }

            // Format output
            var targetResults = targetInfoMap.Values
                .OrderByDescending(t => t.DurationMs)
                .Take(limit)
                .Select(t => new
                {
                    target = t.Name,
                    project = t.Project,
                    succeeded = t.Succeeded,
                    durationMs = Math.Round(t.DurationMs, 1),
                    sourceFile = t.SourceFile,
                    dependsOnTargets = t.DependsOnTargets.Count > 0 ? t.DependsOnTargets : null,
                    triggeredBy = t.TriggeredBy.Count > 0 ? t.TriggeredBy : null,
                    beforeTargets = t.BeforeTargets.Count > 0 ? t.BeforeTargets : null,
                    afterTargets = t.AfterTargets.Count > 0 ? t.AfterTargets : null,
                    executionReasons = t.ExecutionReasons
                })
                .ToList();

            // Find entry point targets (nothing triggered them)
            var entryPoints = targetInfoMap.Values
                .Where(t => t.TriggeredBy.Count == 0 && t.BeforeTargets.Count == 0 && t.AfterTargets.Count == 0)
                .Select(t => new { target = t.Name, project = t.Project })
                .Distinct()
                .Take(20)
                .ToList();

            // Find most-depended-upon targets
            var mostDependedUpon = targetInfoMap.Values
                .Where(t => t.TriggeredBy.Count > 0)
                .OrderByDescending(t => t.TriggeredBy.Count)
                .Take(15)
                .Select(t => new
                {
                    target = t.Name,
                    project = t.Project,
                    triggeredByCount = t.TriggeredBy.Count,
                    triggeredBy = t.TriggeredBy.Take(10).ToList()
                })
                .ToList();

            return new
            {
                file = binlogPath,
                targetFilter,
                projectFilter,
                summary = new
                {
                    totalTargets = targetInfoMap.Count,
                    entryPointTargets = entryPoints.Count,
                    targetsWithDependencies = targetInfoMap.Values.Count(t => t.DependsOnTargets.Count > 0),
                    targetsWithBeforeTargets = targetInfoMap.Values.Count(t => t.BeforeTargets.Count > 0),
                    targetsWithAfterTargets = targetInfoMap.Values.Count(t => t.AfterTargets.Count > 0)
                },
                entryPointTargets = entryPoints.Count > 0 ? entryPoints : null,
                mostDependedUpon = mostDependedUpon.Count > 0 ? mostDependedUpon : null,
                targets = targetResults
            };
        });
    }

    private static List<string> ParseTargetList(string? targetList)
    {
        if (string.IsNullOrWhiteSpace(targetList))
            return [];

        return targetList
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t) && !t.StartsWith("$"))
            .ToList();
    }

    private class TargetExecutionInfo
    {
        public required string Name { get; set; }
        public required string Project { get; set; }
        public string? ProjectFile { get; set; }
        public required List<string> DependsOnTargets { get; set; }
        public required List<string> TriggeredBy { get; set; }
        public required List<string> BeforeTargets { get; set; }
        public required List<string> AfterTargets { get; set; }
        public string? SourceFile { get; set; }
        public double DurationMs { get; set; }
        public bool Succeeded { get; set; }
        public bool WasSkipped { get; set; }
        public required List<string> ExecutionReasons { get; set; }
    }

    [McpServerTool, Description("Lists targets that were skipped during build and explains why (condition was false, already executed, up-to-date, etc.)")]
    public static string GetSkippedTargets(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter to specific project name (optional)")] string? projectFilter = null,
        [Description("Maximum number of skipped targets to return (default: 100)")] int limit = 100)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var messages = new List<Message>();
            var targets = new List<Target>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Message m:
                        messages.Add(m);
                        break;
                    case Target t:
                        targets.Add(t);
                        break;
                }
            });

            // Track executed targets
            var executedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var target in targets)
            {
                var projectName = target.Project?.Name ?? "Unknown";
                if (projectName.FailsFilter(projectFilter))
                    continue;

                executedTargets.Add($"{projectName}:{target.Name}");
            }

            // Find skipped targets from messages
            var skippedTargets = new List<SkippedTargetInfo>();

            foreach (var msg in messages)
            {
                if (string.IsNullOrEmpty(msg.Text))
                    continue;

                var text = msg.Text;
                var projectName = GetProjectName(msg) ?? "Unknown";

                if (projectName.FailsFilter(projectFilter))
                    continue;

                // Look for "Target X skipped" messages
                if (text.Contains("skipped", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Skipping target", StringComparison.OrdinalIgnoreCase))
                {
                    var targetName = text.ExtractTargetName();
                    var reason = text.DetermineSkipReason();

                    if (!string.IsNullOrEmpty(targetName))
                    {
                        skippedTargets.Add(new SkippedTargetInfo
                        {
                            Name = targetName,
                            Project = projectName,
                            Reason = reason,
                            Message = TruncateValue(text, 200)
                        });
                    }
                }

                // Look for "Target X is false" condition messages
                if (text.Contains("condition", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains("false", StringComparison.OrdinalIgnoreCase))
                {
                    var targetName = text.ExtractTargetNameFromCondition();
                    if (!string.IsNullOrEmpty(targetName))
                    {
                        skippedTargets.Add(new SkippedTargetInfo
                        {
                            Name = targetName,
                            Project = projectName,
                            Reason = "Condition evaluated to false",
                            Message = TruncateValue(text, 200)
                        });
                    }
                }

                // Look for "Target X already executed" or "previously built" messages
                if (text.Contains("previously", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("already", StringComparison.OrdinalIgnoreCase))
                {
                    var targetName = text.ExtractTargetName();
                    if (!string.IsNullOrEmpty(targetName))
                    {
                        skippedTargets.Add(new SkippedTargetInfo
                        {
                            Name = targetName,
                            Project = projectName,
                            Reason = "Already executed in this build",
                            Message = TruncateValue(text, 200)
                        });
                    }
                }

                // Look for "up-to-date" messages
                if (text.Contains("up-to-date", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("up to date", StringComparison.OrdinalIgnoreCase))
                {
                    var targetName = text.ExtractTargetName();
                    if (!string.IsNullOrEmpty(targetName))
                    {
                        skippedTargets.Add(new SkippedTargetInfo
                        {
                            Name = targetName,
                            Project = projectName,
                            Reason = "Outputs are up-to-date (incremental build)",
                            Message = TruncateValue(text, 200)
                        });
                    }
                }
            }

            // Deduplicate and limit
            var uniqueSkipped = skippedTargets
                .GroupBy(s => $"{s.Project}:{s.Name}:{s.Reason}")
                .Select(g => g.First())
                .OrderBy(s => s.Project)
                .ThenBy(s => s.Name)
                .Take(limit)
                .ToList();

            // Categorize by reason
            var byReason = uniqueSkipped
                .GroupBy(s => s.Reason ?? "Unknown")
                .Select(g => new
                {
                    reason = g.Key,
                    count = g.Count(),
                    targets = g.Select(t => new { t.Name, t.Project }).Take(10).ToList()
                })
                .OrderByDescending(x => x.count)
                .ToList();

            // Categorize by project
            var byProject = uniqueSkipped
                .GroupBy(s => s.Project)
                .Select(g => new
                {
                    project = g.Key,
                    count = g.Count(),
                    reasons = g.GroupBy(t => t.Reason ?? "Unknown")
                        .Select(rg => new { reason = rg.Key, count = rg.Count() })
                        .OrderByDescending(x => x.count)
                        .ToList()
                })
                .OrderByDescending(x => x.count)
                .ToList();

            return new
            {
                file = binlogPath,
                projectFilter,
                summary = new
                {
                    totalSkippedTargets = uniqueSkipped.Count,
                    executedTargets = executedTargets.Count,
                    skipReasons = byReason.Count
                },
                byReason,
                byProject = byProject.Count > 0 ? byProject : null,
                skippedTargets = uniqueSkipped.Select(s => new
                {
                    target = s.Name,
                    project = s.Project,
                    reason = s.Reason,
                    message = s.Message
                }).ToList()
            };
        });
    }

    private class SkippedTargetInfo
    {
        public required string Name { get; set; }
        public required string Project { get; set; }
        public string? Reason { get; set; }
        public string? Message { get; set; }
    }

    [McpServerTool, Description("Shows where MSBuild properties were defined - traces property values back to their source file and location")]
    public static string GetPropertyOrigin(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Property name to trace (required)")] string propertyName,
        [Description("Filter to specific project (optional)")] string? projectFilter = null)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            return JsonSerializer.Serialize(new { error = "Property name is required" }, JsonOptions);

        return ExecuteBinlogTool(binlogPath, build =>
        {
            var properties = new List<Property>();
            var evaluations = new List<ProjectEvaluation>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Property p:
                        properties.Add(p);
                        break;
                    case ProjectEvaluation pe:
                        evaluations.Add(pe);
                        break;
                }
            });

            // Find all assignments of this property
            var matchingProperties = properties
                .Where(p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Group by project and track assignment order
            var assignmentsByProject = new Dictionary<string, List<PropertyOriginInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in matchingProperties)
            {
                var projectName = GetProjectName(prop) ?? "Global";
                var projectFile = GetProjectFile(prop);

                if (projectName.FailsFilter(projectFilter))
                    continue;

                if (!assignmentsByProject.TryGetValue(projectName, out var assignments))
                {
                    assignments = [];
                    assignmentsByProject[projectName] = assignments;
                }

                // Try to determine the source of the property
                var sourceInfo = DeterminePropertySource(prop);

                assignments.Add(new PropertyOriginInfo
                {
                    Value = prop.Value,
                    Project = projectName,
                    ProjectFile = projectFile,
                    SourceFile = sourceInfo.sourceFile,
                    SourceContext = sourceInfo.context,
                    AssignmentOrder = assignments.Count + 1
                });
            }

            // Format output per project
            var projectResults = assignmentsByProject
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var assignments = kvp.Value;
                    var distinctValues = assignments.Select(a => a.Value).Distinct().ToList();

                    return new
                    {
                        project = kvp.Key,
                        projectFile = assignments.FirstOrDefault()?.ProjectFile,
                        assignmentCount = assignments.Count,
                        hasConflictingValues = distinctValues.Count > 1,
                        finalValue = assignments.LastOrDefault()?.Value,
                        distinctValues = distinctValues.Count > 1 ? distinctValues : null,
                        assignments = assignments.Select(a => new
                        {
                            order = a.AssignmentOrder,
                            value = TruncateValue(a.Value, 200),
                            sourceFile = a.SourceFile,
                            context = a.SourceContext
                        }).ToList()
                    };
                })
                .ToList();

            // Check for global/solution-level values
            var globalValues = matchingProperties
                .Where(p => GetProjectName(p) == null)
                .Select(p => new
                {
                    value = p.Value,
                    source = DeterminePropertySource(p).context
                })
                .DistinctBy(x => x.value)
                .ToList();

            // Determine origin type
            var originSummary = DetermineOriginSummary(matchingProperties);

            return new
            {
                file = binlogPath,
                propertyName,
                projectFilter,
                summary = new
                {
                    totalAssignments = matchingProperties.Count,
                    projectsWithProperty = assignmentsByProject.Count,
                    hasConflicts = projectResults.Any(p => p.hasConflictingValues),
                    likelyOrigin = originSummary
                },
                globalValues = globalValues.Count > 0 ? globalValues : null,
                projectAssignments = projectResults
            };
        });
    }

    private static (string? sourceFile, string? context) DeterminePropertySource(Property prop)
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
                    // Check if it's a known folder type
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

    private static string DetermineOriginSummary(List<Property> properties)
    {
        // Analyze the first few assignments to determine likely origin
        if (properties.Count == 0)
            return "Property not found";

        var contexts = properties
            .Select(p => DeterminePropertySource(p).context)
            .Where(c => c != null)
            .ToList();

        if (contexts.Any(c => c!.Contains("Initial properties")))
            return "Environment variable or command line";
        if (contexts.Any(c => c!.Contains("Directory.Build.props")))
            return "Directory.Build.props";
        if (contexts.Any(c => c!.Contains(".props")))
            return "Imported .props file";
        if (contexts.Any(c => c!.Contains("Target:")))
            return "Set during target execution";
        if (contexts.All(c => c!.Contains("Project:")))
            return "Project file";

        return "Multiple sources";
    }

    private class PropertyOriginInfo
    {
        public string? Value { get; set; }
        public required string Project { get; set; }
        public string? ProjectFile { get; set; }
        public string? SourceFile { get; set; }
        public string? SourceContext { get; set; }
        public int AssignmentOrder { get; set; }
    }

    [McpServerTool, Description("Shows the import chain for a project - which .props and .targets files were imported and in what order")]
    public static string GetImportChain(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter to specific project name (optional)")] string? projectFilter = null,
        [Description("Filter imports by file name pattern (optional)")] string? importFilter = null)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var imports = new List<Import>();
            var projects = new List<Project>();
            var evaluations = new List<ProjectEvaluation>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Import i:
                        imports.Add(i);
                        break;
                    case Project p:
                        projects.Add(p);
                        break;
                    case ProjectEvaluation pe:
                        evaluations.Add(pe);
                        break;
                }
            });

            // Group imports by project
            var importsByProject = new Dictionary<string, List<ImportInfo>>(StringComparer.OrdinalIgnoreCase);

            foreach (var import in imports)
            {
                var projectName = GetProjectName(import) ?? GetProjectFile(import) ?? "Unknown";
                var projectFile = GetProjectFile(import);

                // Apply project filter
                if (projectName.FailsFilter(projectFilter) && projectFile.FailsFilter(projectFilter))
                    continue;

                // Apply import filter
                var importedFile = import.ProjectFilePath ?? import.ImportedProjectFilePath ?? "";
                if (importedFile.FailsFilter(importFilter) && Path.GetFileName(importedFile).FailsFilter(importFilter))
                    continue;

                if (!importsByProject.TryGetValue(projectName, out var projectImports))
                {
                    projectImports = [];
                    importsByProject[projectName] = projectImports;
                }

                // Determine import type
                var importType = importedFile.DetermineImportType();

                projectImports.Add(new ImportInfo
                {
                    ImportedFile = importedFile,
                    ImportedFileName = Path.GetFileName(importedFile),
                    ImportType = importType,
                    ImportingFile = GetProjectFile(import),
                    Line = import.Line,
                    Column = import.Column
                });
            }

            // Also look for imports in evaluation records
            foreach (var eval in evaluations)
            {
                var projectName = eval.Name ?? Path.GetFileNameWithoutExtension(eval.ProjectFile ?? "") ?? "Unknown";

                if (projectName.FailsFilter(projectFilter))
                    continue;

                // Look for import children
                foreach (var child in eval.Children)
                {
                    if (child is Import import)
                    {
                        var importedFile = import.ProjectFilePath ?? import.ImportedProjectFilePath ?? "";

                        if (importedFile.FailsFilter(importFilter))
                            continue;

                        if (!importsByProject.TryGetValue(projectName, out var projectImports))
                        {
                            projectImports = [];
                            importsByProject[projectName] = projectImports;
                        }

                        var importType = importedFile.DetermineImportType();

                        // Avoid duplicates
                        if (!projectImports.Any(i => i.ImportedFile == importedFile))
                        {
                            projectImports.Add(new ImportInfo
                            {
                                ImportedFile = importedFile,
                                ImportedFileName = Path.GetFileName(importedFile),
                                ImportType = importType,
                                ImportingFile = eval.ProjectFile,
                                Line = import.Line,
                                Column = import.Column
                            });
                        }
                    }
                }
            }

            // Format output
            var projectResults = importsByProject
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var projectImports = kvp.Value
                        .OrderBy(i => i.Line)
                        .ToList();

                    // Categorize by type
                    var byType = projectImports
                        .GroupBy(i => i.ImportType)
                        .Select(g => new { type = g.Key, count = g.Count() })
                        .OrderByDescending(x => x.count)
                        .ToList();

                    return new
                    {
                        project = kvp.Key,
                        totalImports = projectImports.Count,
                        importTypes = byType,
                        imports = projectImports.Select(i => new
                        {
                            file = i.ImportedFileName,
                            fullPath = TruncateValue(i.ImportedFile, 150),
                            type = i.ImportType,
                            line = i.Line > 0 ? i.Line : (int?)null,
                            importedBy = i.ImportingFile != null ? Path.GetFileName(i.ImportingFile) : null
                        }).ToList()
                    };
                })
                .ToList();

            // Find common imports (shared across multiple projects)
            var allImports = importsByProject.Values.SelectMany(i => i).ToList();
            var commonImports = allImports
                .GroupBy(i => i.ImportedFileName)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Count())
                .Take(20)
                .Select(g => new
                {
                    file = g.Key,
                    fullPath = g.First().ImportedFile,
                    type = g.First().ImportType,
                    projectCount = g.Select(i => i.ImportingFile).Distinct().Count()
                })
                .ToList();

            // Find SDK imports
            var sdkImports = allImports
                .Where(i => i.ImportType == "SDK" || i.ImportedFile?.Contains("Sdk", StringComparison.OrdinalIgnoreCase) == true)
                .Select(i => i.ImportedFileName)
                .Distinct()
                .ToList();

            // Find Directory.Build imports
            var directoryBuildImports = allImports
                .Where(i => i.ImportedFileName?.StartsWith("Directory.", StringComparison.OrdinalIgnoreCase) == true)
                .Select(i => new
                {
                    file = i.ImportedFileName,
                    fullPath = i.ImportedFile
                })
                .DistinctBy(i => i.file)
                .ToList();

            return new
            {
                file = binlogPath,
                projectFilter,
                importFilter,
                summary = new
                {
                    totalProjects = importsByProject.Count,
                    totalImports = allImports.Count,
                    uniqueImportedFiles = allImports.Select(i => i.ImportedFile).Distinct().Count(),
                    commonImportsCount = commonImports.Count,
                    sdkImportsCount = sdkImports.Count,
                    directoryBuildImportsCount = directoryBuildImports.Count
                },
                sdkImports = sdkImports.Count > 0 ? sdkImports : null,
                directoryBuildImports = directoryBuildImports.Count > 0 ? directoryBuildImports : null,
                commonImports = commonImports.Count > 0 ? commonImports : null,
                projectImports = projectResults
            };
        });
    }

    private class ImportInfo
    {
        public string? ImportedFile { get; set; }
        public string? ImportedFileName { get; set; }
        public string? ImportType { get; set; }
        public string? ImportingFile { get; set; }
        public int Line { get; set; }
        public int Column { get; set; }
    }

    [McpServerTool, Description("Shows target incremental build inputs and outputs - helps debug why targets re-ran or were skipped")]
    public static string GetTargetInputsOutputs(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter to specific target name (optional)")] string? targetFilter = null,
        [Description("Filter to specific project name (optional)")] string? projectFilter = null,
        [Description("Show only targets that ran (not skipped)")] bool executedOnly = false,
        [Description("Maximum number of targets to return (default: 50)")] int limit = 50)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var targets = new List<Target>();
            var messages = new List<Message>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Target t:
                        targets.Add(t);
                        break;
                    case Message m:
                        messages.Add(m);
                        break;
                }
            });

            // Build target info including inputs/outputs from messages
            var targetInputsOutputs = new Dictionary<string, TargetIO>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                var projectName = target.Project?.Name ?? "Unknown";
                var key = $"{projectName}:{target.Name}";

                // Apply filters
                if (target.Name.FailsFilter(targetFilter))
                    continue;

                if (projectName.FailsFilter(projectFilter))
                    continue;

                if (!targetInputsOutputs.ContainsKey(key))
                {
                    var wasSkipped = !target.Children.Any() &&
                        GetDurationMs(target.StartTime, target.EndTime) < 1;

                    if (executedOnly && wasSkipped)
                        continue;

                    targetInputsOutputs[key] = new TargetIO
                    {
                        Name = target.Name,
                        Project = projectName,
                        ProjectFile = target.Project?.ProjectFile,
                        DurationMs = GetDurationMs(target.StartTime, target.EndTime),
                        Succeeded = target.Succeeded,
                        WasSkipped = wasSkipped,
                        SourceFile = target.SourceFilePath,
                        Inputs = [],
                        Outputs = [],
                        InputFiles = [],
                        OutputFiles = [],
                        SkipReason = null
                    };
                }
            }

            // Parse messages for input/output information
            // MSBuild logs messages about target inputs and outputs
            foreach (var msg in messages)
            {
                if (string.IsNullOrEmpty(msg.Text))
                    continue;

                var text = msg.Text;
                var projectName = GetProjectName(msg) ?? "Unknown";

                // Look for "Building target X" or "Skipping target X" with input/output info
                // Common patterns:
                // - "Building target 'X' completely."
                // - "Input file 'X' is newer than output file 'Y'"
                // - "Skipping target 'X' because all output files are up-to-date"
                // - "Output file 'X' does not exist"
                // - "Input 'X' is newer than output 'Y'"

                string? targetName = null;

                // Extract target name from message
                targetName = text.ExtractTargetName();
                if (targetName == null) continue;

                var key = $"{projectName}:{targetName}";
                if (!targetInputsOutputs.TryGetValue(key, out var targetIO))
                {
                    // Check if this target matches filters
                    if (targetName.FailsFilter(targetFilter)) continue;
                    if (projectName.FailsFilter(projectFilter)) continue;

                    targetIO = new TargetIO
                    {
                        Name = targetName,
                        Project = projectName,
                        ProjectFile = null,
                        DurationMs = 0,
                        Succeeded = true,
                        WasSkipped = text.Contains("skipping", StringComparison.OrdinalIgnoreCase) ||
                                    text.Contains("up-to-date", StringComparison.OrdinalIgnoreCase),
                        SourceFile = null,
                        Inputs = [],
                        Outputs = [],
                        InputFiles = [],
                        OutputFiles = [],
                        SkipReason = null
                    };
                    targetInputsOutputs[key] = targetIO;
                }

                // Extract skip reason
                if (text.Contains("skipping", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("up-to-date", StringComparison.OrdinalIgnoreCase))
                {
                    targetIO.SkipReason ??= text.DetermineSkipReason();
                    targetIO.WasSkipped = true;
                }

                // Extract input/output files from message
                ExtractFilesFromMessage(text, targetIO);

                // Store the raw message for analysis
                targetIO.Inputs.Add(TruncateValue(text, 200) ?? "");
            }

            // Also look for task parameters that indicate inputs/outputs
            foreach (var kvp in targetInputsOutputs)
            {
                var targetIO = kvp.Value;
                var matchingTarget = targets.FirstOrDefault(t =>
                    t.Name == targetIO.Name &&
                    (t.Project?.Name ?? "Unknown") == targetIO.Project);

                if (matchingTarget != null)
                {
                    // Look for common input/output tasks
                    foreach (var child in matchingTarget.Children)
                    {
                        if (child is Microsoft.Build.Logging.StructuredLogger.Task task)
                        {
                            ExtractTaskInputsOutputs(task, targetIO);
                        }
                    }
                }
            }

            // Format results
            var results = targetInputsOutputs.Values
                .OrderByDescending(t => t.DurationMs)
                .Take(limit)
                .Select(t => new
                {
                    target = t.Name,
                    project = t.Project,
                    durationMs = Math.Round(t.DurationMs, 1),
                    succeeded = t.Succeeded,
                    wasSkipped = t.WasSkipped,
                    skipReason = t.SkipReason,
                    inputFileCount = t.InputFiles.Count,
                    outputFileCount = t.OutputFiles.Count,
                    inputFiles = t.InputFiles.Count > 0 ? t.InputFiles.Take(10).ToList() : null,
                    outputFiles = t.OutputFiles.Count > 0 ? t.OutputFiles.Take(10).ToList() : null,
                    sourceFile = t.SourceFile != null ? Path.GetFileName(t.SourceFile) : null
                })
                .ToList();

            // Summary statistics
            var executed = targetInputsOutputs.Values.Where(t => !t.WasSkipped).ToList();
            var skipped = targetInputsOutputs.Values.Where(t => t.WasSkipped).ToList();
            var withInputs = targetInputsOutputs.Values.Where(t => t.InputFiles.Count > 0).ToList();
            var withOutputs = targetInputsOutputs.Values.Where(t => t.OutputFiles.Count > 0).ToList();

            // Skip reason breakdown
            var skipReasons = skipped
                .Where(t => t.SkipReason != null)
                .GroupBy(t => t.SkipReason)
                .Select(g => new { reason = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToList();

            return new
            {
                file = binlogPath,
                targetFilter,
                projectFilter,
                executedOnly,
                summary = new
                {
                    totalTargets = targetInputsOutputs.Count,
                    executed = executed.Count,
                    skipped = skipped.Count,
                    withInputFiles = withInputs.Count,
                    withOutputFiles = withOutputs.Count,
                    totalExecutionTimeMs = Math.Round(executed.Sum(t => t.DurationMs), 1)
                },
                skipReasons = skipReasons.Count > 0 ? skipReasons : null,
                targets = results
            };
        });
    }

    private static void ExtractFilesFromMessage(string text, TargetIO targetIO)
    {
        // Look for file paths in the message
        // Common patterns:
        // - Input file 'path'
        // - Output file 'path'
        // - 'path' is newer/older than 'path'

        var words = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < words.Length; i++)
        {
            var word = words[i].Trim('\'', '"', ',', ';', '.');

            // Check if this looks like a file path
            if (LooksLikeFilePath(word))
            {
                var fileName = Path.GetFileName(word);

                // Determine if it's an input or output based on context
                var context = i > 0 ? words[i - 1].ToLowerInvariant() : "";
                var nextWord = i < words.Length - 1 ? words[i + 1].ToLowerInvariant() : "";

                if (context == "input" || context == "source" ||
                    nextWord == "newer" || nextWord == "changed")
                {
                    if (!targetIO.InputFiles.Contains(fileName))
                        targetIO.InputFiles.Add(fileName);
                }
                else if (context == "output" || context == "target" ||
                         text.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                {
                    if (!targetIO.OutputFiles.Contains(fileName))
                        targetIO.OutputFiles.Add(fileName);
                }
            }
        }
    }

    private static void ExtractTaskInputsOutputs(Microsoft.Build.Logging.StructuredLogger.Task task, TargetIO targetIO)
    {
        // Look for parameters that commonly indicate inputs/outputs
        var inputParams = new[] { "Sources", "SourceFiles", "InputFiles", "Files", "References", "Compile" };
        var outputParams = new[] { "OutputAssembly", "OutputFile", "DestinationFiles", "DestinationFolder", "Output" };

        foreach (var child in task.Children)
        {
            if (child is not Parameter param) continue;

            var isInput = inputParams.Any(p => param.Name.Contains(p, StringComparison.OrdinalIgnoreCase));
            var isOutput = outputParams.Any(p => param.Name.Contains(p, StringComparison.OrdinalIgnoreCase));

            if (!isInput && !isOutput) continue;

            foreach (var item in param.Children.OfType<Item>())
            {
                var fileName = Path.GetFileName(item.Text ?? "");
                if (string.IsNullOrEmpty(fileName)) continue;

                if (isInput && !targetIO.InputFiles.Contains(fileName))
                    targetIO.InputFiles.Add(fileName);
                else if (isOutput && !targetIO.OutputFiles.Contains(fileName))
                    targetIO.OutputFiles.Add(fileName);
            }
        }
    }

    private static bool LooksLikeFilePath(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length < 3) return false;

        // Check for common path patterns
        return text.Contains('.') &&
               (text.Contains('\\') || text.Contains('/') ||
                text.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) ||
                text.EndsWith(".props", StringComparison.OrdinalIgnoreCase));
    }

    private class TargetIO
    {
        public required string Name { get; set; }
        public required string Project { get; set; }
        public string? ProjectFile { get; set; }
        public double DurationMs { get; set; }
        public bool Succeeded { get; set; }
        public bool WasSkipped { get; set; }
        public string? SourceFile { get; set; }
        public string? SkipReason { get; set; }
        public required List<string> Inputs { get; set; }
        public required List<string> Outputs { get; set; }
        public required List<string> InputFiles { get; set; }
        public required List<string> OutputFiles { get; set; }
    }
}
