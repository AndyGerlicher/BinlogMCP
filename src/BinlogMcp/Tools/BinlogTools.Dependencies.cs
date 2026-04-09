using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BinlogMcp.Tools;

public static partial class BinlogTools
{
    [McpServerTool, Description("Gets project dependency graph showing which projects depend on which, build order, and parallel vs sequential execution")]
    public static string GetProjectDependencies(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Include target-level details showing which targets triggered child builds (default: false)")] bool includeTargetDetails = false)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Project Dependencies", build =>
        {
            var projects = new List<Project>();
            var addItems = new List<AddItem>();
            var msbuildTasks = new List<MSBuildTask>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Project p:
                        projects.Add(p);
                        break;
                    case AddItem a:
                        addItems.Add(a);
                        break;
                    case MSBuildTask t when includeTargetDetails &&
                        (t.Name.Equals("MSBuild", StringComparison.OrdinalIgnoreCase) ||
                         t.Name.Equals("CallTarget", StringComparison.OrdinalIgnoreCase)):
                        msbuildTasks.Add(t);
                        break;
                }
            });

            // Build a map of project file path to project info
            var projectMap = new Dictionary<string, ProjectInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in projects)
            {
                var projectFile = project.ProjectFile;
                if (string.IsNullOrEmpty(projectFile))
                    continue;

                if (!projectMap.TryGetValue(projectFile, out var info))
                {
                    info = new ProjectInfo
                    {
                        Name = project.Name ?? Path.GetFileNameWithoutExtension(projectFile),
                        ProjectFile = projectFile,
                        TargetFramework = project.TargetFramework,
                        Dependencies = [],
                        StartTime = project.StartTime,
                        EndTime = project.EndTime
                    };
                    projectMap[projectFile] = info;
                }
                else
                {
                    // Update times to encompass all evaluations
                    if (project.StartTime < info.StartTime)
                        info.StartTime = project.StartTime;
                    if (project.EndTime > info.EndTime)
                        info.EndTime = project.EndTime;
                }
            }

            // Find ProjectReference items to build dependency graph
            foreach (var addItem in addItems)
            {
                if (!string.Equals(addItem.Name, "ProjectReference", StringComparison.OrdinalIgnoreCase))
                    continue;

                var ownerProject = GetProjectFile(addItem);
                if (string.IsNullOrEmpty(ownerProject))
                    continue;

                foreach (var item in addItem.Children.OfType<Item>())
                {
                    var referencedProject = item.Text;
                    if (string.IsNullOrEmpty(referencedProject))
                        continue;

                    // Resolve relative path
                    var ownerDir = Path.GetDirectoryName(ownerProject);
                    if (!string.IsNullOrEmpty(ownerDir))
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(ownerDir, referencedProject));
                        if (projectMap.TryGetValue(ownerProject, out var ownerInfo))
                        {
                            if (!ownerInfo.Dependencies.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                            {
                                ownerInfo.Dependencies.Add(fullPath);
                            }
                        }
                    }
                }
            }

            // Calculate build order by start time
            var buildOrder = projectMap.Values
                .OrderBy(p => p.StartTime)
                .Select((p, index) => new { project = p, order = index + 1 })
                .ToDictionary(x => x.project.ProjectFile, x => x.order, StringComparer.OrdinalIgnoreCase);

            // Detect parallelism by checking for overlapping time ranges
            var projectList = projectMap.Values.ToList();
            var parallelGroups = DetectParallelGroups(projectList);

            // Build output
            var projectsOutput = projectMap.Values
                .OrderBy(p => p.StartTime)
                .Select(p => new
                {
                    name = p.Name,
                    projectFile = p.ProjectFile,
                    targetFramework = p.TargetFramework,
                    buildOrder = buildOrder.GetValueOrDefault(p.ProjectFile, 0),
                    durationMs = GetDurationMs(p.StartTime, p.EndTime),
                    durationFormatted = FormatDuration(GetDuration(p.StartTime, p.EndTime)),
                    startTime = p.StartTime.ToTimeString(),
                    endTime = p.EndTime.ToTimeString(),
                    dependsOn = p.Dependencies
                        .Select(d => projectMap.TryGetValue(d, out var dep) ? dep.Name : Path.GetFileNameWithoutExtension(d))
                        .ToList(),
                    dependencyCount = p.Dependencies.Count
                })
                .ToList();

            // Find root projects (no other project depends on them being built first)
            var allDependencies = projectMap.Values.SelectMany(p => p.Dependencies).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rootProjects = projectMap.Keys.Where(p => !allDependencies.Contains(p)).ToList();

            // Build target-level invocation details if requested
            object? targetInvocations = null;
            if (includeTargetDetails && msbuildTasks.Count > 0)
            {
                var invocations = msbuildTasks
                    .Select(task =>
                    {
                        var callerProject = GetProjectName(task);
                        var callerTarget = GetParentTargetName(task);

                        var projectsParam = task.Children.OfType<Parameter>()
                            .FirstOrDefault(p => p.Name.Equals("Projects", StringComparison.OrdinalIgnoreCase));
                        var targetProjects = projectsParam?.Children.OfType<Item>()
                            .Select(i => Path.GetFileName(i.Text))
                            .Where(p => !string.IsNullOrEmpty(p))
                            .ToList() ?? [];

                        var targetsParam = task.Children.OfType<Parameter>()
                            .FirstOrDefault(p => p.Name.Equals("Targets", StringComparison.OrdinalIgnoreCase));
                        var targetTargets = targetsParam?.Children.OfType<Item>()
                            .Select(i => i.Text)
                            .Where(t => !string.IsNullOrEmpty(t))
                            .ToList() ?? [];

                        return new
                        {
                            callerProject,
                            callerTarget,
                            targetProjects,
                            targetsInvoked = targetTargets,
                            durationMs = Math.Round(GetDurationMs(task.StartTime, task.EndTime), 1)
                        };
                    })
                    .Where(i => i.targetProjects.Count > 0 || i.targetsInvoked.Count > 0)
                    .OrderByDescending(i => i.durationMs)
                    .ToList();

                // Group by caller to show call patterns
                var byCallerProject = invocations
                    .GroupBy(i => i.callerProject ?? "Unknown")
                    .Select(g => new
                    {
                        project = g.Key,
                        callCount = g.Count(),
                        callerTargets = g.Select(i => i.callerTarget).Where(t => t != null).Distinct().ToList(),
                        childProjectsCalled = g.SelectMany(i => i.targetProjects).Distinct().ToList(),
                        childTargetsInvoked = g.SelectMany(i => i.targetsInvoked).Where(t => t != null).Distinct().ToList()
                    })
                    .OrderByDescending(x => x.callCount)
                    .ToList();

                targetInvocations = new
                {
                    totalInvocations = invocations.Count,
                    byCallerProject,
                    recentCalls = invocations.Take(30).ToList()
                };
            }

            var result = new
            {
                file = binlogPath,
                includeTargetDetails,
                projectCount = projectMap.Count,
                rootProjects = rootProjects.Select(p => projectMap[p].Name).ToList(),
                parallelism = new
                {
                    maxParallelProjects = parallelGroups.Count > 0 ? parallelGroups.Max(g => g.Count) : 1,
                    parallelGroups = parallelGroups.Select(g => g.Select(p => p.Name).ToList()).ToList()
                },
                projects = projectsOutput,
                targetInvocations
            };

            return result;
        });
    }

    private static List<List<ProjectInfo>> DetectParallelGroups(List<ProjectInfo> projects)
    {
        if (projects.Count == 0)
            return [];

        var groups = new List<List<ProjectInfo>>();
        var sorted = projects.OrderBy(p => p.StartTime).ToList();

        var currentGroup = new List<ProjectInfo> { sorted[0] };
        var groupEndTime = sorted[0].EndTime;

        for (int i = 1; i < sorted.Count; i++)
        {
            var project = sorted[i];
            // If this project started before the current group ended, it ran in parallel
            if (project.StartTime < groupEndTime)
            {
                currentGroup.Add(project);
                if (project.EndTime > groupEndTime)
                    groupEndTime = project.EndTime;
            }
            else
            {
                // Only add groups with more than one project (actual parallelism)
                if (currentGroup.Count > 1)
                    groups.Add(currentGroup);
                currentGroup = [project];
                groupEndTime = project.EndTime;
            }
        }

        if (currentGroup.Count > 1)
            groups.Add(currentGroup);

        return groups;
    }

    private class ProjectInfo
    {
        public required string Name { get; set; }
        public required string ProjectFile { get; set; }
        public string? TargetFramework { get; set; }
        public required List<string> Dependencies { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    [McpServerTool, Description("Analyzes NuGet package restore from a binlog - shows packages, timing, and any restore issues")]
    public static string GetNuGetRestoreAnalysis(
        [Description("Path to the binlog file")] string binlogPath)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var targets = new List<Target>();
            var messages = new List<Message>();
            var addItems = new List<AddItem>();
            var warnings = new List<Warning>();
            var errors = new List<Error>();

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
                    case AddItem a:
                        addItems.Add(a);
                        break;
                    case Warning w:
                        warnings.Add(w);
                        break;
                    case Error e:
                        errors.Add(e);
                        break;
                }
            });

            // Find restore targets
            var restoreTargets = targets
                .Where(t => t.Name.Contains("Restore", StringComparison.OrdinalIgnoreCase) ||
                           t.Name.Contains("NuGet", StringComparison.OrdinalIgnoreCase))
                .Select(t => new
                {
                    name = t.Name,
                    project = t.Project?.Name,
                    durationMs = GetDurationMs(t.StartTime, t.EndTime),
                    succeeded = t.Succeeded
                })
                .OrderByDescending(t => t.durationMs)
                .ToList();

            var totalRestoreTime = restoreTargets.Sum(t => t.durationMs);

            // Find PackageReference items
            var packageReferences = addItems
                .Where(a => a.Name.Equals("PackageReference", StringComparison.OrdinalIgnoreCase))
                .SelectMany(a => a.Children.OfType<Item>().Select(item =>
                {
                    var version = item.Children.OfType<Metadata>()
                        .FirstOrDefault(m => m.Name.Equals("Version", StringComparison.OrdinalIgnoreCase))?.Value;
                    return new
                    {
                        package = item.Text,
                        version,
                        project = GetProjectFile(a)
                    };
                }))
                .DistinctBy(p => $"{p.project}:{p.package}")
                .OrderBy(p => p.package)
                .ToList();

            // Find restore-related messages
            var restoreMessages = messages
                .Where(m => m.Text != null && (
                    m.Text.Contains("Restoring packages", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("restored", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("PackageReference", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("nuget.org", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("package source", StringComparison.OrdinalIgnoreCase)))
                .Select(m => m.Text)
                .Distinct()
                .Take(30)
                .ToList();

            // Find restore warnings
            var restoreWarnings = warnings
                .Where(w => w.Code != null && (
                    w.Code.StartsWith("NU", StringComparison.OrdinalIgnoreCase) ||
                    (w.Text != null && w.Text.Contains("package", StringComparison.OrdinalIgnoreCase))))
                .Select(w => new
                {
                    code = w.Code,
                    message = w.Text,
                    project = w.ProjectFile
                })
                .DistinctBy(w => $"{w.code}:{w.message}")
                .ToList();

            // Find restore errors
            var restoreErrors = errors
                .Where(e => e.Code != null && (
                    e.Code.StartsWith("NU", StringComparison.OrdinalIgnoreCase) ||
                    (e.Text != null && e.Text.Contains("package", StringComparison.OrdinalIgnoreCase))))
                .Select(e => new
                {
                    code = e.Code,
                    message = e.Text,
                    project = e.ProjectFile
                })
                .DistinctBy(e => $"{e.code}:{e.message}")
                .ToList();

            // Summary by project
            var packagesByProject = packageReferences
                .GroupBy(p => Path.GetFileName(p.project) ?? "Unknown")
                .Select(g => new
                {
                    project = g.Key,
                    packageCount = g.Count(),
                    packages = g.Select(p => new { p.package, p.version }).ToList()
                })
                .OrderByDescending(p => p.packageCount)
                .ToList();

            return new
            {
                file = binlogPath,
                summary = new
                {
                    totalRestoreTimeMs = totalRestoreTime,
                    totalRestoreTimeFormatted = FormatDuration(TimeSpan.FromMilliseconds(totalRestoreTime)),
                    totalPackages = packageReferences.Count,
                    projectsWithPackages = packagesByProject.Count,
                    restoreWarnings = restoreWarnings.Count,
                    restoreErrors = restoreErrors.Count
                },
                restoreTargets = restoreTargets.Take(20).ToList(),
                packagesByProject,
                restoreWarnings = restoreWarnings.Count > 0 ? restoreWarnings : null,
                restoreErrors = restoreErrors.Count > 0 ? restoreErrors : null,
                restoreMessages = restoreMessages.Count > 0 ? restoreMessages : null
            };
        });
    }

    [McpServerTool, Description("Analyzes assembly references from a binlog - shows all references, their paths, and any issues")]
    public static string GetAssemblyReferences(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Filter by project name (optional)")] string? projectFilter = null)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Assembly References", build =>
        {
            var addItems = new List<AddItem>();
            var warnings = new List<Warning>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case AddItem a:
                        addItems.Add(a);
                        break;
                    case Warning w:
                        warnings.Add(w);
                        break;
                }
            });

            // Find Reference items (assembly references)
            var assemblyReferences = addItems
                .Where(a => a.Name.Equals("Reference", StringComparison.OrdinalIgnoreCase))
                .SelectMany(a => a.Children.OfType<Item>().Select(item =>
                {
                    var hintPath = item.Children.OfType<Metadata>()
                        .FirstOrDefault(m => m.Name.Equals("HintPath", StringComparison.OrdinalIgnoreCase))?.Value;
                    var projectFile = GetProjectFile(a);
                    return new
                    {
                        assembly = item.Text,
                        hintPath,
                        project = Path.GetFileName(projectFile),
                        projectFile
                    };
                }))
                .Where(r => r.project.MatchesFilter(projectFilter))
                .DistinctBy(r => $"{r.projectFile}:{r.assembly}")
                .OrderBy(r => r.assembly)
                .ToList();

            // Find ProjectReference items
            var projectReferences = addItems
                .Where(a => a.Name.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase))
                .SelectMany(a => a.Children.OfType<Item>().Select(item =>
                {
                    var projectFile = GetProjectFile(a);
                    return new
                    {
                        referencedProject = Path.GetFileName(item.Text),
                        fullPath = item.Text,
                        fromProject = Path.GetFileName(projectFile),
                        fromProjectFile = projectFile
                    };
                }))
                .Where(r => r.fromProject.MatchesFilter(projectFilter))
                .DistinctBy(r => $"{r.fromProjectFile}:{r.referencedProject}")
                .OrderBy(r => r.referencedProject)
                .ToList();

            // Find reference-related warnings
            var referenceWarnings = warnings
                .Where(w => w.Code != null && (
                    w.Code.StartsWith("CS", StringComparison.OrdinalIgnoreCase) ||
                    w.Code.StartsWith("MSB", StringComparison.OrdinalIgnoreCase)) &&
                    w.Text != null && (
                        w.Text.Contains("reference", StringComparison.OrdinalIgnoreCase) ||
                        w.Text.Contains("assembly", StringComparison.OrdinalIgnoreCase)))
                .Select(w => new
                {
                    code = w.Code,
                    message = w.Text,
                    project = Path.GetFileName(w.ProjectFile)
                })
                .DistinctBy(w => $"{w.code}:{w.message}")
                .ToList();

            // Group assembly references by project
            var referencesByProject = assemblyReferences
                .GroupBy(r => r.project ?? "Unknown")
                .Select(g => new
                {
                    project = g.Key,
                    referenceCount = g.Count(),
                    references = g.Select(r => new { r.assembly, r.hintPath }).Take(20).ToList()
                })
                .OrderByDescending(p => p.referenceCount)
                .ToList();

            var uniqueAssemblies = assemblyReferences.Select(r => r.assembly).Distinct().Count();

            return new
            {
                file = binlogPath,
                projectFilter,
                summary = new
                {
                    totalReferences = assemblyReferences.Count,
                    uniqueAssemblies,
                    projectReferences = projectReferences.Count,
                    projectsWithReferences = referencesByProject.Count,
                    referenceWarnings = referenceWarnings.Count
                },
                referencesByProject,
                projectReferences,
                referenceWarnings = referenceWarnings.Count > 0 ? referenceWarnings : null
            };
        });
    }

    [McpServerTool, Description("Detects projects that are built but whose outputs aren't referenced by any other project, which may indicate dead code or unnecessary build work")]
    public static string GetUnusedProjectOutputs(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Exclude test projects from analysis (default: true)")] bool excludeTests = true)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var projects = new List<Project>();
            var addItems = new List<AddItem>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Project p:
                        projects.Add(p);
                        break;
                    case AddItem a:
                        addItems.Add(a);
                        break;
                }
            });

            // Build a map of all projects with their output assemblies
            var projectInfos = new Dictionary<string, UnusedProjectInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in projects)
            {
                var projectFile = project.ProjectFile;
                if (string.IsNullOrEmpty(projectFile))
                    continue;

                if (!projectInfos.ContainsKey(projectFile))
                {
                    var name = project.Name ?? Path.GetFileNameWithoutExtension(projectFile);
                    var assemblyName = name;

                    var isTestProject = name.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                                       name.Contains("Tests", StringComparison.OrdinalIgnoreCase) ||
                                       name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
                                       name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase);

                    projectInfos[projectFile] = new UnusedProjectInfo
                    {
                        Name = name,
                        ProjectFile = projectFile,
                        AssemblyName = assemblyName,
                        TargetFramework = project.TargetFramework,
                        IsTestProject = isTestProject,
                        DurationMs = GetDurationMs(project.StartTime, project.EndTime),
                        ReferencedBy = []
                    };
                }
            }

            // Collect all project references
            var referencedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var addItem in addItems.Where(a => a.Name.Equals("ProjectReference", StringComparison.OrdinalIgnoreCase)))
            {
                var ownerProjectFile = GetProjectFile(addItem);
                if (string.IsNullOrEmpty(ownerProjectFile))
                    continue;

                foreach (var item in addItem.Children.OfType<Item>())
                {
                    var referencedPath = item.Text;
                    if (string.IsNullOrEmpty(referencedPath))
                        continue;

                    var ownerDir = Path.GetDirectoryName(ownerProjectFile);
                    if (!string.IsNullOrEmpty(ownerDir))
                    {
                        var fullPath = Path.GetFullPath(Path.Combine(ownerDir, referencedPath));
                        referencedProjects.Add(fullPath);

                        if (projectInfos.TryGetValue(fullPath, out var refProject))
                        {
                            var ownerName = projectInfos.TryGetValue(ownerProjectFile, out var owner)
                                ? owner.Name
                                : Path.GetFileNameWithoutExtension(ownerProjectFile);
                            if (!refProject.ReferencedBy.Contains(ownerName))
                                refProject.ReferencedBy.Add(ownerName);
                        }
                    }
                }
            }

            // Check for assembly references that might match project outputs
            var assemblyReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var addItem in addItems.Where(a => a.Name.Equals("Reference", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var item in addItem.Children.OfType<Item>())
                {
                    if (!string.IsNullOrEmpty(item.Text))
                    {
                        var assemblyName = item.Text.Split(',')[0].Trim();
                        assemblyReferences.Add(assemblyName);
                    }
                }
            }

            // Find unused projects
            var unusedProjects = projectInfos.Values
                .Where(p => !referencedProjects.Contains(p.ProjectFile) &&
                           !assemblyReferences.Contains(p.AssemblyName) &&
                           (!excludeTests || !p.IsTestProject))
                .OrderByDescending(p => p.DurationMs)
                .ToList();

            // Categorize unused projects
            var entryPoints = unusedProjects
                .Where(p => p.Name.EndsWith(".App", StringComparison.OrdinalIgnoreCase) ||
                           p.Name.EndsWith(".Exe", StringComparison.OrdinalIgnoreCase) ||
                           p.Name.Contains("Program", StringComparison.OrdinalIgnoreCase) ||
                           p.Name.Contains("Console", StringComparison.OrdinalIgnoreCase) ||
                           p.Name.Contains("Web", StringComparison.OrdinalIgnoreCase) ||
                           p.Name.Contains("Api", StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToList();

            var potentiallyUnused = unusedProjects
                .Where(p => !entryPoints.Contains(p.Name))
                .ToList();

            var totalWastedTime = potentiallyUnused.Sum(p => p.DurationMs);
            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            return new
            {
                file = binlogPath,
                excludeTests,
                summary = new
                {
                    totalProjects = projectInfos.Count,
                    unreferencedProjects = unusedProjects.Count,
                    likelyEntryPoints = entryPoints.Count,
                    potentiallyUnused = potentiallyUnused.Count,
                    potentialWastedTimeMs = Math.Round(totalWastedTime, 1),
                    potentialWastedTimeFormatted = FormatDuration(TimeSpan.FromMilliseconds(totalWastedTime)),
                    wastedPercentOfBuild = buildDuration > 0 ? Math.Round(totalWastedTime / buildDuration * 100, 1) : 0
                },
                likelyEntryPoints = entryPoints.Count > 0 ? entryPoints : null,
                potentiallyUnusedProjects = potentiallyUnused.Select(p => new
                {
                    name = p.Name,
                    projectFile = p.ProjectFile,
                    targetFramework = p.TargetFramework,
                    durationMs = Math.Round(p.DurationMs, 1),
                    durationFormatted = FormatDuration(TimeSpan.FromMilliseconds(p.DurationMs)),
                    isTestProject = p.IsTestProject
                }).ToList(),
                allUnreferencedProjects = unusedProjects.Select(p => new
                {
                    name = p.Name,
                    projectFile = p.ProjectFile,
                    isTestProject = p.IsTestProject,
                    isLikelyEntryPoint = entryPoints.Contains(p.Name)
                }).ToList()
            };
        });
    }

    private class UnusedProjectInfo
    {
        public required string Name { get; set; }
        public required string ProjectFile { get; set; }
        public required string AssemblyName { get; set; }
        public string? TargetFramework { get; set; }
        public bool IsTestProject { get; set; }
        public double DurationMs { get; set; }
        public required List<string> ReferencedBy { get; set; }
    }

    [McpServerTool, Description("Analyzes target dependency graph showing which targets depend on which, finds circular dependencies and redundant dependency chains")]
    public static string GetTargetDependencyGraph(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter to specific project name (optional)")] string? projectFilter = null,
        [Description("Filter to specific target name (optional)")] string? targetFilter = null)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var targets = new List<Target>();
            build.VisitAllChildren<Target>(t => targets.Add(t));

            // Build target info map
            var targetInfos = new Dictionary<string, TargetDepInfo>(StringComparer.OrdinalIgnoreCase);
            var projectTargets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var target in targets)
            {
                var projectName = target.Project?.Name ?? "Unknown";
                var key = $"{projectName}:{target.Name}";

                if (projectName.FailsFilter(projectFilter))
                    continue;

                if (target.Name.FailsFilter(targetFilter))
                    continue;

                if (!targetInfos.ContainsKey(key))
                {
                    var dependsOn = target.DependsOnTargets?
                        .Split([';'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(d => d.Trim())
                        .Where(d => !string.IsNullOrEmpty(d))
                        .ToList() ?? [];

                    targetInfos[key] = new TargetDepInfo
                    {
                        Name = target.Name,
                        Project = projectName,
                        DependsOn = dependsOn,
                        DependedOnBy = [],
                        DurationMs = GetDurationMs(target.StartTime, target.EndTime),
                        WasExecuted = true,
                        WasSkipped = !target.Succeeded && target.Children.Count == 0
                    };

                    if (!projectTargets.TryGetValue(projectName, out var projTargetList))
                    {
                        projTargetList = [];
                        projectTargets[projectName] = projTargetList;
                    }
                    if (!projTargetList.Contains(target.Name))
                        projTargetList.Add(target.Name);
                }
            }

            // Build reverse dependency map
            foreach (var kvp in targetInfos)
            {
                foreach (var dep in kvp.Value.DependsOn)
                {
                    var depKey = $"{kvp.Value.Project}:{dep}";
                    if (targetInfos.TryGetValue(depKey, out var depTarget))
                    {
                        if (!depTarget.DependedOnBy.Contains(kvp.Value.Name))
                            depTarget.DependedOnBy.Add(kvp.Value.Name);
                    }
                }
            }

            // Detect circular dependencies
            var circularDeps = new List<object>();
            var visited = new HashSet<string>();
            var recursionStack = new HashSet<string>();

            foreach (var kvp in targetInfos)
            {
                var cycle = FindTargetCycle(kvp.Key, targetInfos, visited, recursionStack, []);
                if (cycle != null && cycle.Count > 0)
                {
                    circularDeps.Add(new
                    {
                        project = kvp.Value.Project,
                        cycle = cycle
                    });
                }
            }

            // Find root and leaf targets
            var rootTargets = targetInfos.Values
                .Where(t => t.DependedOnBy.Count == 0)
                .Select(t => new { t.Name, t.Project })
                .ToList();

            var leafTargets = targetInfos.Values
                .Where(t => t.DependsOn.Count == 0 && t.DependedOnBy.Count > 0)
                .Select(t => new { t.Name, t.Project, dependedOnBy = t.DependedOnBy.Count })
                .OrderByDescending(t => t.dependedOnBy)
                .ToList();

            // Find bottlenecks
            var bottlenecks = targetInfos.Values
                .Where(t => t.DependedOnBy.Count >= 3)
                .OrderByDescending(t => t.DependedOnBy.Count)
                .Select(t => new
                {
                    target = t.Name,
                    project = t.Project,
                    dependedOnByCount = t.DependedOnBy.Count,
                    dependedOnBy = t.DependedOnBy.Take(10).ToList(),
                    durationMs = Math.Round(t.DurationMs, 1)
                })
                .Take(20)
                .ToList();

            // Find redundant dependencies
            var redundantDeps = new List<object>();
            foreach (var kvp in targetInfos)
            {
                var deps = kvp.Value.DependsOn;
                if (deps.Count < 2) continue;

                foreach (var dep1 in deps)
                {
                    var dep1Key = $"{kvp.Value.Project}:{dep1}";
                    if (!targetInfos.TryGetValue(dep1Key, out var dep1Info)) continue;

                    foreach (var dep2 in deps)
                    {
                        if (dep1 == dep2) continue;
                        if (dep1Info.DependsOn.Contains(dep2))
                        {
                            redundantDeps.Add(new
                            {
                                target = kvp.Value.Name,
                                project = kvp.Value.Project,
                                directDep = dep2,
                                transitiveVia = dep1,
                                reason = $"{kvp.Value.Name} directly depends on {dep2}, but {dep1} already depends on {dep2}"
                            });
                        }
                    }
                }
            }

            var graphOutput = targetInfos.Values
                .OrderByDescending(t => t.DependedOnBy.Count)
                .ThenByDescending(t => t.DurationMs)
                .Take(50)
                .Select(t => new
                {
                    target = t.Name,
                    project = t.Project,
                    dependsOn = t.DependsOn,
                    dependedOnBy = t.DependedOnBy,
                    durationMs = Math.Round(t.DurationMs, 1),
                    wasSkipped = t.WasSkipped
                })
                .ToList();

            return new
            {
                file = binlogPath,
                projectFilter,
                targetFilter,
                summary = new
                {
                    totalTargets = targetInfos.Count,
                    projectsAnalyzed = projectTargets.Count,
                    rootTargets = rootTargets.Count,
                    leafTargets = leafTargets.Count,
                    circularDependencies = circularDeps.Count,
                    redundantDependencies = redundantDeps.Count,
                    potentialBottlenecks = bottlenecks.Count
                },
                circularDependencies = circularDeps.Count > 0 ? circularDeps.Take(10).ToList() : null,
                redundantDependencies = redundantDeps.Count > 0 ? redundantDeps.Take(20).ToList() : null,
                bottlenecks = bottlenecks.Count > 0 ? bottlenecks : null,
                rootTargets = rootTargets.Take(20).ToList(),
                leafTargets = leafTargets.Take(20).ToList(),
                dependencyGraph = graphOutput
            };
        });
    }

    private static List<string>? FindTargetCycle(
        string startKey,
        Dictionary<string, TargetDepInfo> targetInfos,
        HashSet<string> visited,
        HashSet<string> recursionStack,
        List<string> path)
    {
        if (recursionStack.Contains(startKey))
        {
            var cycleStart = path.IndexOf(startKey.Split(':').Last());
            if (cycleStart >= 0)
                return path.Skip(cycleStart).Append(startKey.Split(':').Last()).ToList();
            return [startKey.Split(':').Last()];
        }

        if (visited.Contains(startKey))
            return null;

        visited.Add(startKey);
        recursionStack.Add(startKey);

        if (targetInfos.TryGetValue(startKey, out var info))
        {
            path.Add(info.Name);
            foreach (var dep in info.DependsOn)
            {
                var depKey = $"{info.Project}:{dep}";
                var cycle = FindTargetCycle(depKey, targetInfos, visited, recursionStack, path);
                if (cycle != null)
                    return cycle;
            }
            path.RemoveAt(path.Count - 1);
        }

        recursionStack.Remove(startKey);
        return null;
    }

    private class TargetDepInfo
    {
        public required string Name { get; set; }
        public required string Project { get; set; }
        public required List<string> DependsOn { get; set; }
        public required List<string> DependedOnBy { get; set; }
        public double DurationMs { get; set; }
        public bool WasExecuted { get; set; }
        public bool WasSkipped { get; set; }
    }

    [McpServerTool, Description("Detects SDK and framework version mismatches across projects - finds TargetFramework inconsistencies, package version conflicts, and runtime mismatches")]
    public static string GetSdkFrameworkMismatch(
        [Description("Path to the binlog file")] string binlogPath)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var projects = new List<Project>();
            var properties = new List<Property>();
            var addItems = new List<AddItem>();
            var warnings = new List<Warning>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Project p:
                        projects.Add(p);
                        break;
                    case Property prop:
                        properties.Add(prop);
                        break;
                    case AddItem a:
                        addItems.Add(a);
                        break;
                    case Warning w:
                        warnings.Add(w);
                        break;
                }
            });

            // Collect framework info per project
            var projectFrameworks = new Dictionary<string, ProjectFrameworkInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var project in projects)
            {
                var projectFile = project.ProjectFile;
                if (string.IsNullOrEmpty(projectFile))
                    continue;

                if (!projectFrameworks.ContainsKey(projectFile))
                {
                    projectFrameworks[projectFile] = new ProjectFrameworkInfo
                    {
                        ProjectFile = projectFile,
                        ProjectName = project.Name ?? Path.GetFileNameWithoutExtension(projectFile),
                        TargetFramework = project.TargetFramework,
                        Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        PackageReferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    };
                }
            }

            // Collect important properties per project
            var importantProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "TargetFramework", "TargetFrameworks", "TargetFrameworkVersion",
                "RuntimeIdentifier", "RuntimeIdentifiers", "PlatformTarget",
                "LangVersion", "Nullable", "ImplicitUsings",
                "SdkVersion", "NetCoreSdkVersion", "MSBuildSDKsPath",
                "NETStandardImplicitPackageVersion", "RuntimeFrameworkVersion"
            };

            foreach (var prop in properties)
            {
                if (!importantProps.Contains(prop.Name))
                    continue;

                var projectFile = GetProjectFile(prop);
                if (string.IsNullOrEmpty(projectFile) || !projectFrameworks.TryGetValue(projectFile, out var info))
                    continue;

                if (!info.Properties.ContainsKey(prop.Name))
                    info.Properties[prop.Name] = prop.Value;
            }

            // Collect package references per project
            foreach (var addItem in addItems.Where(a => a.Name.Equals("PackageReference", StringComparison.OrdinalIgnoreCase)))
            {
                var projectFile = GetProjectFile(addItem);
                if (string.IsNullOrEmpty(projectFile) || !projectFrameworks.TryGetValue(projectFile, out var info))
                    continue;

                foreach (var item in addItem.Children.OfType<Item>())
                {
                    var packageName = item.Text;
                    var version = item.Children.OfType<Metadata>()
                        .FirstOrDefault(m => m.Name.Equals("Version", StringComparison.OrdinalIgnoreCase))?.Value;

                    if (!string.IsNullOrEmpty(packageName))
                    {
                        info.PackageReferences[packageName] = version ?? "unknown";
                    }
                }
            }

            // Detect TargetFramework mismatches
            var frameworkVersions = projectFrameworks.Values
                .Where(p => !string.IsNullOrEmpty(p.TargetFramework))
                .GroupBy(p => p.TargetFramework!)
                .Select(g => new
                {
                    framework = g.Key,
                    count = g.Count(),
                    projects = g.Select(p => p.ProjectName).ToList()
                })
                .OrderByDescending(x => x.count)
                .ToList();

            var hasFrameworkMismatch = frameworkVersions.Count > 1;

            // Detect LangVersion mismatches
            var langVersions = projectFrameworks.Values
                .Where(p => p.Properties.ContainsKey("LangVersion"))
                .GroupBy(p => p.Properties["LangVersion"])
                .Select(g => new
                {
                    langVersion = g.Key,
                    count = g.Count(),
                    projects = g.Select(p => p.ProjectName).ToList()
                })
                .OrderByDescending(x => x.count)
                .ToList();

            var hasLangVersionMismatch = langVersions.Count > 1;

            // Detect RuntimeIdentifier mismatches
            var runtimeIds = projectFrameworks.Values
                .Where(p => p.Properties.ContainsKey("RuntimeIdentifier") || p.Properties.ContainsKey("RuntimeIdentifiers"))
                .Select(p => new
                {
                    project = p.ProjectName,
                    rid = p.Properties.TryGetValue("RuntimeIdentifier", out var rid) ? rid :
                          p.Properties.TryGetValue("RuntimeIdentifiers", out var rids) ? rids : null
                })
                .Where(x => !string.IsNullOrEmpty(x.rid))
                .GroupBy(x => x.rid!)
                .Select(g => new
                {
                    runtimeIdentifier = g.Key,
                    count = g.Count(),
                    projects = g.Select(x => x.project).ToList()
                })
                .ToList();

            // Detect PlatformTarget mismatches
            var platforms = projectFrameworks.Values
                .Where(p => p.Properties.ContainsKey("PlatformTarget"))
                .GroupBy(p => p.Properties["PlatformTarget"])
                .Select(g => new
                {
                    platform = g.Key,
                    count = g.Count(),
                    projects = g.Select(p => p.ProjectName).ToList()
                })
                .OrderByDescending(x => x.count)
                .ToList();

            var hasPlatformMismatch = platforms.Count > 1;

            // Detect package version conflicts
            var packageVersions = projectFrameworks.Values
                .SelectMany(p => p.PackageReferences.Select(pr => new
                {
                    project = p.ProjectName,
                    package = pr.Key,
                    version = pr.Value
                }))
                .GroupBy(x => x.package)
                .Where(g => g.Select(x => x.version).Distinct().Count() > 1)
                .Select(g => new
                {
                    package = g.Key,
                    versions = g.GroupBy(x => x.version)
                        .Select(vg => new
                        {
                            version = vg.Key,
                            projects = vg.Select(x => x.project).ToList()
                        })
                        .OrderByDescending(x => x.projects.Count)
                        .ToList()
                })
                .OrderByDescending(x => x.versions.Count)
                .ToList();

            // Find relevant warnings
            var mismatchWarnings = warnings
                .Where(w => w.Code != null && (
                    w.Code.StartsWith("MSB3277", StringComparison.OrdinalIgnoreCase) ||
                    w.Code.StartsWith("MSB3243", StringComparison.OrdinalIgnoreCase) ||
                    w.Code.StartsWith("MSB3270", StringComparison.OrdinalIgnoreCase) ||
                    w.Code.StartsWith("NU1605", StringComparison.OrdinalIgnoreCase) ||
                    w.Code.StartsWith("NU1608", StringComparison.OrdinalIgnoreCase)))
                .Select(w => new
                {
                    code = w.Code,
                    message = TruncateValue(w.Text, 200),
                    project = Path.GetFileName(w.ProjectFile)
                })
                .DistinctBy(w => $"{w.code}:{w.message}")
                .ToList();

            // Detect Nullable setting mismatches
            var nullableSettings = projectFrameworks.Values
                .Where(p => p.Properties.ContainsKey("Nullable"))
                .GroupBy(p => p.Properties["Nullable"])
                .Select(g => new
                {
                    nullable = g.Key,
                    count = g.Count(),
                    projects = g.Select(p => p.ProjectName).ToList()
                })
                .ToList();

            var hasNullableMismatch = nullableSettings.Count > 1;

            // Generate recommendations
            var recommendations = new List<string>();
            if (hasFrameworkMismatch)
                recommendations.Add("Consider standardizing TargetFramework across all projects using Directory.Build.props");
            if (hasLangVersionMismatch)
                recommendations.Add("Standardize LangVersion in Directory.Build.props to ensure consistent C# features");
            if (hasPlatformMismatch)
                recommendations.Add("Align PlatformTarget settings to avoid runtime issues");
            if (hasNullableMismatch)
                recommendations.Add("Consider enabling Nullable consistently across the solution");
            if (packageVersions.Count > 0)
                recommendations.Add("Use Central Package Management (Directory.Packages.props) to consolidate package versions");
            if (mismatchWarnings.Count > 0)
                recommendations.Add("Resolve assembly reference conflicts to prevent runtime errors");

            return new
            {
                file = binlogPath,
                summary = new
                {
                    totalProjects = projectFrameworks.Count,
                    frameworkMismatch = hasFrameworkMismatch,
                    langVersionMismatch = hasLangVersionMismatch,
                    platformMismatch = hasPlatformMismatch,
                    nullableMismatch = hasNullableMismatch,
                    packageVersionConflicts = packageVersions.Count,
                    mismatchWarnings = mismatchWarnings.Count
                },
                targetFrameworks = frameworkVersions,
                langVersions = langVersions.Count > 0 ? langVersions : null,
                runtimeIdentifiers = runtimeIds.Count > 0 ? runtimeIds : null,
                platformTargets = platforms.Count > 0 ? platforms : null,
                nullableSettings = nullableSettings.Count > 0 ? nullableSettings : null,
                packageVersionConflicts = packageVersions.Count > 0 ? packageVersions.Take(20).ToList() : null,
                mismatchWarnings = mismatchWarnings.Count > 0 ? mismatchWarnings : null,
                recommendations = recommendations.Count > 0 ? recommendations : null
            };
        });
    }

    private class ProjectFrameworkInfo
    {
        public required string ProjectFile { get; set; }
        public required string ProjectName { get; set; }
        public string? TargetFramework { get; set; }
        public required Dictionary<string, string> Properties { get; set; }
        public required Dictionary<string, string> PackageReferences { get; set; }
    }
}
