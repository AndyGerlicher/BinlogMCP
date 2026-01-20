using BinlogMcp.Formatting;
using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using MSBuildTask = Microsoft.Build.Logging.StructuredLogger.Task;

namespace BinlogMcp.Tools;

public static partial class BinlogTools
{
    [McpServerTool, Description("Gets target execution details including timing information, sorted by duration (slowest first)")]
    public static string GetTargets(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv, timeline")] string format = "json",
        [Description("Filter targets by project name (optional)")] string? projectFilter = null,
        [Description("Include skipped targets with skip reasons (default: false)")] bool includeSkipped = false,
        [Description("Maximum number of targets to return (default: 50)")] int limit = 50)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Target Execution", build =>
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
                    case Message m when includeSkipped:
                        messages.Add(m);
                        break;
                }
            });

            var targetData = targets
                .Where(t => t.Project?.Name.MatchesFilter(projectFilter) ?? string.IsNullOrEmpty(projectFilter))
                .Select(t => new
                {
                    name = t.Name,
                    project = t.Project?.Name,
                    succeeded = t.Succeeded,
                    skipped = false,
                    skipReason = (string?)null,
                    durationMs = GetDurationMs(t.StartTime, t.EndTime),
                    durationFormatted = FormatDuration(GetDuration(t.StartTime, t.EndTime)),
                    startTime = t.StartTime.ToTimeString(),
                    endTime = t.EndTime.ToTimeString()
                })
                .OrderByDescending(t => t.durationMs)
                .ToList();

            // If includeSkipped, find skipped targets from messages
            var skippedTargets = new List<object>();
            if (includeSkipped)
            {
                var executedTargetKeys = targets
                    .Select(t => $"{t.Project?.Name}:{t.Name}")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var skippedInfo = new Dictionary<string, (string project, string reason, string? message)>(StringComparer.OrdinalIgnoreCase);

                foreach (var msg in messages)
                {
                    if (string.IsNullOrEmpty(msg.Text)) continue;
                    var text = msg.Text;
                    var projectName = GetProjectName(msg) ?? "Unknown";

                    if (projectName.FailsFilter(projectFilter))
                        continue;

                    // Look for skip patterns
                    if (text.Contains("skipped", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("Skipping target", StringComparison.OrdinalIgnoreCase) ||
                        (text.Contains("condition", StringComparison.OrdinalIgnoreCase) && text.Contains("false", StringComparison.OrdinalIgnoreCase)))
                    {
                        var targetName = text.ExtractTargetName();
                        if (!string.IsNullOrEmpty(targetName))
                        {
                            var key = $"{projectName}:{targetName}";
                            if (!executedTargetKeys.Contains(key) && !skippedInfo.ContainsKey(key))
                            {
                                var reason = text.DetermineSkipReason();
                                skippedInfo[key] = (projectName, reason, TruncateValue(text, 150));
                            }
                        }
                    }
                }

                skippedTargets = skippedInfo
                    .Select(kvp => new
                    {
                        name = kvp.Key.Split(':').Last(),
                        project = kvp.Value.project,
                        succeeded = false,
                        skipped = true,
                        skipReason = kvp.Value.reason,
                        durationMs = 0.0,
                        durationFormatted = "0ms",
                        startTime = (string?)null,
                        endTime = (string?)null
                    })
                    .Cast<object>()
                    .ToList();
            }

            var totalDuration = targets.Sum(t => GetDurationMs(t.StartTime, t.EndTime));

            // Combine executed and skipped if requested
            var allTargets = includeSkipped
                ? targetData.Cast<object>().Concat(skippedTargets).Take(limit).ToList()
                : targetData.Take(limit).Cast<object>().ToList();

            return new
            {
                file = binlogPath,
                projectFilter,
                includeSkipped,
                totalTargets = targets.Count,
                skippedTargetCount = includeSkipped ? skippedTargets.Count : (int?)null,
                returnedTargets = allTargets.Count,
                totalDurationMs = totalDuration,
                totalDurationFormatted = FormatDuration(TimeSpan.FromMilliseconds(totalDuration)),
                targets = allTargets
            };
        });
    }

    [McpServerTool, Description("Gets task execution details including timing, sorted by duration (slowest first)")]
    public static string GetTasks(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv, timeline")] string format = "json",
        [Description("Filter tasks by name (optional)")] string? taskFilter = null,
        [Description("Maximum number of tasks to return (default: 50)")] int limit = 50)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Task Execution", build =>
        {
            var tasks = new List<MSBuildTask>();
            build.VisitAllChildren<MSBuildTask>(t => tasks.Add(t));

            var taskData = tasks
                .Where(t => t.Name.MatchesFilter(taskFilter))
                .Select(t => new
                {
                    name = t.Name,
                    sourceFile = t.FromAssembly,
                    parentTarget = t.Parent is Target target ? target.Name : null,
                    project = GetProjectName(t),
                    durationMs = GetDurationMs(t.StartTime, t.EndTime),
                    durationFormatted = FormatDuration(GetDuration(t.StartTime, t.EndTime)),
                    startTime = t.StartTime.ToTimeString(),
                    endTime = t.EndTime.ToTimeString()
                })
                .OrderByDescending(t => t.durationMs)
                .Take(limit)
                .ToList();

            // Aggregate by task name for summary
            var taskSummary = tasks
                .GroupBy(t => t.Name)
                .Select(g => new
                {
                    name = g.Key,
                    invocations = g.Count(),
                    totalDurationMs = g.Sum(t => GetDurationMs(t.StartTime, t.EndTime)),
                    avgDurationMs = g.Average(t => GetDurationMs(t.StartTime, t.EndTime))
                })
                .OrderByDescending(t => t.totalDurationMs)
                .Take(10)
                .ToList();

            return new
            {
                file = binlogPath,
                taskFilter,
                totalTasks = tasks.Count,
                returnedTasks = taskData.Count,
                slowestTaskTypes = taskSummary,
                tasks = taskData
            };
        });
    }

    [McpServerTool, Description("Gets the critical path - the longest chain of sequential operations that determined the build duration")]
    public static string GetCriticalPath(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, timeline")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Critical Path", build =>
        {
            var targets = new List<Target>();
            build.VisitAllChildren<Target>(t => targets.Add(t));

            // Find targets on the critical path by looking at the longest chain
            // Simple heuristic: targets that ended closest to the build end time
            // and took significant time are likely on the critical path
            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            var criticalTargets = targets
                .Where(t => GetDurationMs(t.StartTime, t.EndTime) > 0)
                .OrderByDescending(t => t.EndTime)
                .ThenByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                .Take(20)
                .Select(t =>
                {
                    var durationMs = GetDurationMs(t.StartTime, t.EndTime);
                    return new
                    {
                        name = t.Name,
                        project = t.Project?.Name,
                        durationMs,
                        durationFormatted = FormatDuration(GetDuration(t.StartTime, t.EndTime)),
                        startTime = t.StartTime.ToTimeString(),
                        endTime = t.EndTime.ToTimeString(),
                        percentOfBuild = buildDuration > 0 ? Math.Round(durationMs / buildDuration * 100, 1) : 0
                    };
                })
                .ToList();

            return new
            {
                file = binlogPath,
                buildDurationMs = buildDuration,
                buildDurationFormatted = FormatDuration(TimeSpan.FromMilliseconds(buildDuration)),
                criticalPathTargets = criticalTargets
            };
        });
    }

    [McpServerTool, Description("Comprehensive build performance analysis - identifies bottlenecks, slow targets/tasks, and optimization opportunities")]
    public static string GetPerformanceReport(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv, timeline")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Performance Report", build =>
        {
            var targets = new List<Target>();
            var tasks = new List<MSBuildTask>();
            var projects = new List<Project>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Target t:
                        targets.Add(t);
                        break;
                    case MSBuildTask task:
                        tasks.Add(task);
                        break;
                    case Project p:
                        projects.Add(p);
                        break;
                }
            });

            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            // Slowest targets
            var slowestTargets = targets
                .OrderByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                .Take(15)
                .Select(t =>
                {
                    var durationMs = GetDurationMs(t.StartTime, t.EndTime);
                    return new
                    {
                        name = t.Name,
                        project = t.Project?.Name,
                        durationMs = Math.Round(durationMs, 1),
                        percentOfBuild = Math.Round(durationMs / buildDuration * 100, 1)
                    };
                })
                .ToList();

            // Slowest tasks
            var slowestTasks = tasks
                .OrderByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                .Take(15)
                .Select(t =>
                {
                    var durationMs = GetDurationMs(t.StartTime, t.EndTime);
                    return new
                    {
                        name = t.Name,
                        project = GetProjectName(t),
                        target = t.Parent is Target target ? target.Name : null,
                        durationMs = Math.Round(durationMs, 1),
                        percentOfBuild = Math.Round(durationMs / buildDuration * 100, 1)
                    };
                })
                .ToList();

            // Task type summary
            var taskTypeSummary = tasks
                .GroupBy(t => t.Name)
                .Select(g =>
                {
                    var totalMs = g.Sum(t => GetDurationMs(t.StartTime, t.EndTime));
                    return new
                    {
                        name = g.Key,
                        count = g.Count(),
                        totalMs = Math.Round(totalMs, 1),
                        avgMs = Math.Round(g.Average(t => GetDurationMs(t.StartTime, t.EndTime)), 1),
                        percentOfBuild = Math.Round(totalMs / buildDuration * 100, 1)
                    };
                })
                .OrderByDescending(x => x.totalMs)
                .Take(15)
                .ToList();

            // Project timing
            var projectTiming = projects
                .DistinctBy(p => p.ProjectFile)
                .Select(p =>
                {
                    var durationMs = GetDurationMs(p.StartTime, p.EndTime);
                    return new
                    {
                        name = p.Name,
                        durationMs = Math.Round(durationMs, 1),
                        percentOfBuild = Math.Round(durationMs / buildDuration * 100, 1)
                    };
                })
                .OrderByDescending(p => p.durationMs)
                .Take(10)
                .ToList();

            // Bottleneck hints
            var hints = new List<string>();
            var cscTime = tasks
                .Where(t => t.Name.Equals("Csc", StringComparison.OrdinalIgnoreCase))
                .Sum(t => GetDurationMs(t.StartTime, t.EndTime));
            if (cscTime > buildDuration * 0.5)
            {
                hints.Add($"Compilation takes {Math.Round(cscTime / buildDuration * 100)}% of build time. Consider incremental builds or splitting large projects.");
            }

            var copyTime = tasks
                .Where(t => t.Name.Equals("Copy", StringComparison.OrdinalIgnoreCase))
                .Sum(t => GetDurationMs(t.StartTime, t.EndTime));
            if (copyTime > buildDuration * 0.2)
            {
                hints.Add($"File copying takes {Math.Round(copyTime / buildDuration * 100)}% of build time. Consider reducing copied files or using symbolic links.");
            }

            return new
            {
                file = binlogPath,
                summary = new
                {
                    buildDurationMs = Math.Round(buildDuration, 1),
                    buildDurationFormatted = FormatDuration(TimeSpan.FromMilliseconds(buildDuration)),
                    succeeded = build.Succeeded,
                    totalTargets = targets.Count,
                    totalTasks = tasks.Count,
                    totalProjects = projects.DistinctBy(p => p.ProjectFile).Count()
                },
                slowestTargets,
                slowestTasks,
                taskTypeSummary,
                projectTiming,
                optimizationHints = hints.Count > 0 ? hints : null
            };
        });
    }

    [McpServerTool, Description("Detailed C# compilation performance analysis - Csc task timing, files compiled, and compiler options")]
    public static string GetCompilerPerformance(
        [Description("Path to the binlog file")] string binlogPath)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var tasks = new List<MSBuildTask>();
            build.VisitAllChildren<MSBuildTask>(t => tasks.Add(t));

            // Find all compiler tasks
            var compilerTaskNames = new[] { "Csc", "Vbc", "Fsc" };
            var compilerTasks = tasks
                .Where(t => compilerTaskNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var totalCompileTime = compilerTasks.Sum(t => GetDurationMs(t.StartTime, t.EndTime));
            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            // Summary by compiler type
            var compilerSummary = compilerTasks
                .GroupBy(t => t.Name)
                .Select(g => new
                {
                    compiler = g.Key,
                    invocations = g.Count(),
                    totalMs = Math.Round(g.Sum(t => GetDurationMs(t.StartTime, t.EndTime)), 1),
                    avgMs = Math.Round(g.Average(t => GetDurationMs(t.StartTime, t.EndTime)), 1)
                })
                .ToList();

            // Per-project compilation details
            var compilationByProject = compilerTasks
                .OrderByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                .Select(t =>
                {
                    var sourceCount = 0;
                    foreach (var child in t.Children)
                    {
                        if (child is Folder folder && folder.Name.Equals("Sources", StringComparison.OrdinalIgnoreCase))
                        {
                            sourceCount = folder.Children.Count;
                            break;
                        }
                    }

                    var outputValue = GetParameterValue(t, "OutputAssembly");
                    var outputAssembly = outputValue != null ? Path.GetFileName(outputValue) : "";
                    var durationMs = GetDurationMs(t.StartTime, t.EndTime);

                    return new
                    {
                        project = GetProjectName(t),
                        compiler = t.Name,
                        durationMs = Math.Round(durationMs, 1),
                        sourceFiles = sourceCount,
                        outputAssembly,
                        msPerFile = sourceCount > 0 ? Math.Round(durationMs / sourceCount, 1) : 0
                    };
                })
                .Take(20)
                .ToList();

            return new
            {
                file = binlogPath,
                summary = new
                {
                    totalCompileTimeMs = Math.Round(totalCompileTime, 1),
                    totalCompileTimeFormatted = FormatDuration(TimeSpan.FromMilliseconds(totalCompileTime)),
                    compilerInvocations = compilerTasks.Count,
                    percentOfBuild = buildDuration > 0 ? Math.Round(totalCompileTime / buildDuration * 100, 1) : 0
                },
                compilerSummary,
                compilationByProject
            };
        });
    }

    [McpServerTool, Description("Analyzes build parallelism - shows concurrent operations, sequential bottlenecks, and parallelization efficiency")]
    public static string GetParallelismAnalysis(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, timeline")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Parallelism Analysis", build =>
        {
            var projects = new List<Project>();
            var targets = new List<Target>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Project p:
                        projects.Add(p);
                        break;
                    case Target t:
                        targets.Add(t);
                        break;
                }
            });

            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);
            var buildStart = build.StartTime;

            // Get unique projects
            var uniqueProjects = projects
                .GroupBy(p => p.ProjectFile)
                .Select(g => new
                {
                    Name = g.First().Name,
                    ProjectFile = g.Key,
                    StartTime = g.Min(p => p.StartTime),
                    EndTime = g.Max(p => p.EndTime)
                })
                .ToList();

            // Calculate parallelism over time (sample at intervals)
            var sampleCount = 20;
            var intervalMs = buildDuration / sampleCount;
            var parallelismOverTime = new List<object>();

            for (int i = 0; i < sampleCount; i++)
            {
                var sampleTime = buildStart.AddMilliseconds(i * intervalMs);
                var activeProjects = uniqueProjects.Count(p => p.StartTime <= sampleTime && p.EndTime >= sampleTime);
                parallelismOverTime.Add(new
                {
                    timeMs = Math.Round(i * intervalMs),
                    activeProjects
                });
            }

            var maxParallel = parallelismOverTime.Count > 0
                ? parallelismOverTime.Max(p => ((dynamic)p).activeProjects)
                : 1;
            var avgParallel = parallelismOverTime.Count > 0
                ? parallelismOverTime.Average(p => (double)((dynamic)p).activeProjects)
                : 1;

            // Find sequential bottlenecks (projects that ran alone for significant time)
            var projectOverlaps = uniqueProjects
                .Select(p =>
                {
                    var overlapping = uniqueProjects.Count(other =>
                        other.ProjectFile != p.ProjectFile &&
                        other.StartTime < p.EndTime &&
                        other.EndTime > p.StartTime);
                    var durationMs = GetDurationMs(p.StartTime, p.EndTime);
                    return new
                    {
                        project = p.Name,
                        durationMs = Math.Round(durationMs, 1),
                        overlappingProjects = overlapping,
                        isSequentialBottleneck = overlapping == 0 && durationMs > buildDuration * 0.1
                    };
                })
                .OrderByDescending(p => p.durationMs)
                .ToList();

            var sequentialBottlenecks = projectOverlaps.Where(p => p.isSequentialBottleneck).ToList();

            // Calculate theoretical speedup potential
            var totalProjectTime = uniqueProjects.Sum(p => GetDurationMs(p.StartTime, p.EndTime));
            var theoreticalMinTime = totalProjectTime / Math.Max(maxParallel, 1);
            var parallelEfficiency = theoreticalMinTime > 0 ? Math.Round(theoreticalMinTime / buildDuration * 100, 1) : 100;

            return new
            {
                file = binlogPath,
                summary = new
                {
                    buildDurationMs = Math.Round(buildDuration, 1),
                    totalProjectTime = Math.Round(totalProjectTime, 1),
                    maxParallelProjects = maxParallel,
                    avgParallelProjects = Math.Round(avgParallel, 1),
                    parallelEfficiencyPercent = parallelEfficiency,
                    sequentialBottleneckCount = sequentialBottlenecks.Count
                },
                parallelismOverTime,
                projectOverlaps = projectOverlaps.Take(15).ToList(),
                sequentialBottlenecks = sequentialBottlenecks.Count > 0 ? sequentialBottlenecks : null
            };
        });
    }

    [McpServerTool, Description("Analyzes slow file operations - Copy, Move, Delete tasks and other I/O-bound operations")]
    public static string GetSlowOperations(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Minimum duration in milliseconds to include (default: 10)")] int minDurationMs = 10)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var tasks = new List<MSBuildTask>();
            build.VisitAllChildren<MSBuildTask>(t => tasks.Add(t));

            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            var ioTasks = tasks
                .Where(t => IoTaskNames.Contains(t.Name))
                .Where(t => GetDurationMs(t.StartTime, t.EndTime) >= minDurationMs)
                .OrderByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                .ToList();

            // Summary by task type
            var taskTypeSummary = ioTasks
                .GroupBy(t => t.Name)
                .Select(g => new
                {
                    taskType = g.Key,
                    count = g.Count(),
                    totalMs = Math.Round(g.Sum(t => GetDurationMs(t.StartTime, t.EndTime)), 1),
                    avgMs = Math.Round(g.Average(t => GetDurationMs(t.StartTime, t.EndTime)), 1),
                    maxMs = Math.Round(g.Max(t => GetDurationMs(t.StartTime, t.EndTime)), 1)
                })
                .OrderByDescending(x => x.totalMs)
                .ToList();

            var totalCopyTime = tasks
                .Where(t => t.Name.Equals("Copy", StringComparison.OrdinalIgnoreCase))
                .Sum(t => GetDurationMs(t.StartTime, t.EndTime));

            var totalExecTime = tasks
                .Where(t => t.Name.Equals("Exec", StringComparison.OrdinalIgnoreCase))
                .Sum(t => GetDurationMs(t.StartTime, t.EndTime));

            var slowestTasks = ioTasks
                .Take(30)
                .Select(t =>
                {
                    var details = TruncateValue(
                        GetFirstParameterValue(t, "Command", "SourceFiles"),
                        100) ?? "";

                    return new
                    {
                        task = t.Name,
                        project = GetProjectName(t),
                        target = t.Parent is Target target ? target.Name : null,
                        durationMs = Math.Round(GetDurationMs(t.StartTime, t.EndTime), 1),
                        details
                    };
                })
                .ToList();

            return new
            {
                file = binlogPath,
                minDurationMs,
                summary = new
                {
                    buildDurationMs = Math.Round(buildDuration, 1),
                    totalCopyTimeMs = Math.Round(totalCopyTime, 1),
                    totalExecTimeMs = Math.Round(totalExecTime, 1),
                    ioTaskCount = ioTasks.Count,
                    ioPercentOfBuild = Math.Round((totalCopyTime + totalExecTime) / buildDuration * 100, 1)
                },
                taskTypeSummary,
                slowestTasks
            };
        });
    }

    [McpServerTool, Description("Detects tasks that run multiple times with identical or similar inputs, indicating redundant/wasted work")]
    public static string GetRedundantOperations(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Minimum number of executions to consider redundant (default: 2)")] int minExecutions = 2)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var taskExecutions = new Dictionary<string, List<RedundantTaskInfo>>();

            build.VisitAllChildren<MSBuildTask>(task =>
            {
                var signature = GetTaskSignature(task);
                if (!taskExecutions.TryGetValue(signature, out var list))
                {
                    list = [];
                    taskExecutions[signature] = list;
                }

                list.Add(new RedundantTaskInfo
                {
                    TaskName = task.Name,
                    Project = GetProjectName(task),
                    Target = task.Parent is Target t ? t.Name : null,
                    DurationMs = GetDurationMs(task.StartTime, task.EndTime),
                    StartTime = task.StartTime,
                    InputHash = GetTaskInputHash(task)
                });
            });

            // Find redundant executions (same signature, multiple times)
            var redundantTasks = taskExecutions
                .Where(kv => kv.Value.Count >= minExecutions)
                .OrderByDescending(kv => kv.Value.Sum(e => e.DurationMs))
                .Select(kv =>
                {
                    var executions = kv.Value;
                    var totalTime = executions.Sum(e => e.DurationMs);
                    var wastedTime = totalTime - executions.Max(e => e.DurationMs);

                    return new
                    {
                        taskName = executions.First().TaskName,
                        executionCount = executions.Count,
                        totalTimeMs = Math.Round(totalTime, 1),
                        wastedTimeMs = Math.Round(wastedTime, 1),
                        signature = TruncateValue(kv.Key, 150),
                        executions = executions
                            .OrderBy(e => e.StartTime)
                            .Select(e => new
                            {
                                project = e.Project,
                                target = e.Target,
                                durationMs = Math.Round(e.DurationMs, 1),
                                time = e.StartTime.ToTimeString()
                            })
                            .ToList()
                    };
                })
                .ToList();

            // Group by task type for summary
            var byTaskType = redundantTasks
                .GroupBy(r => r.taskName)
                .Select(g => new
                {
                    taskName = g.Key,
                    redundantCalls = g.Sum(r => r.executionCount - 1),
                    totalWastedMs = Math.Round(g.Sum(r => r.wastedTimeMs), 1),
                    instances = g.Count()
                })
                .OrderByDescending(x => x.totalWastedMs)
                .ToList();

            var totalWastedTime = redundantTasks.Sum(r => r.wastedTimeMs);
            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            return new
            {
                file = binlogPath,
                minExecutions,
                summary = new
                {
                    redundantTaskGroups = redundantTasks.Count,
                    totalRedundantExecutions = redundantTasks.Sum(r => r.executionCount - 1),
                    totalWastedTimeMs = Math.Round(totalWastedTime, 1),
                    wastedTimeFormatted = FormatDuration(TimeSpan.FromMilliseconds(totalWastedTime)),
                    wastedPercentOfBuild = buildDuration > 0 ? Math.Round(totalWastedTime / buildDuration * 100, 1) : 0
                },
                byTaskType,
                redundantTasks = redundantTasks.Take(30).ToList()
            };
        });
    }

    private static string GetTaskSignature(MSBuildTask task)
    {
        var parts = new List<string> { task.Name };

        foreach (var child in task.Children)
        {
            if (child is Parameter param)
            {
                var importantParams = new[] { "Sources", "SourceFiles", "InputFiles", "OutputAssembly", "References" };
                if (importantParams.Contains(param.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var values = param.Children.OfType<Item>().Select(i => i.Text).Take(3);
                    parts.Add($"{param.Name}={string.Join(";", values)}");
                }
            }
        }

        return string.Join("|", parts);
    }

    private static string GetTaskInputHash(MSBuildTask task)
    {
        var inputs = new List<string>();
        foreach (var child in task.Children)
        {
            if (child is Parameter param && !param.Name.Equals("MSBuildSourceProjectFile", StringComparison.OrdinalIgnoreCase))
            {
                var firstItem = param.Children.OfType<Item>().FirstOrDefault();
                inputs.Add($"{param.Name}:{param.Children.Count}:{firstItem?.Text?.GetHashCode() ?? 0}");
            }
        }
        return string.Join(",", inputs.Take(5));
    }

    private class RedundantTaskInfo
    {
        public required string TaskName { get; set; }
        public string? Project { get; set; }
        public string? Target { get; set; }
        public double DurationMs { get; set; }
        public DateTime StartTime { get; set; }
        public string? InputHash { get; set; }
    }

    [McpServerTool, Description("Per-project performance breakdown - shows timing rollup by project to identify which projects are the slowest")]
    public static string GetProjectPerformance(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Include detailed target breakdown per project (default: false)")] bool includeTargetDetails = false,
        [Description("Maximum number of projects to return (default: 50)")] int limit = 50)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Project Performance", build =>
        {
            var targets = new List<Target>();
            var tasks = new List<MSBuildTask>();
            var projects = new List<Project>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Target t:
                        targets.Add(t);
                        break;
                    case MSBuildTask task:
                        tasks.Add(task);
                        break;
                    case Project p:
                        projects.Add(p);
                        break;
                }
            });

            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);
            var buildStart = build.StartTime;
            var buildEnd = build.EndTime;

            // Group by project file for accurate timing
            var projectData = projects
                .GroupBy(p => p.ProjectFile)
                .Select(g =>
                {
                    var projName = g.First().Name;
                    var projFile = g.Key ?? "Unknown";

                    // Get overall project timing (first start to last end)
                    var projStart = g.Min(p => p.StartTime);
                    var projEnd = g.Max(p => p.EndTime);
                    var wallClockMs = GetDurationMs(projStart, projEnd);

                    // Get targets for this project
                    var projTargets = targets.Where(t => t.Project?.ProjectFile == projFile).ToList();
                    var targetTimeMs = projTargets.Sum(t => GetDurationMs(t.StartTime, t.EndTime));

                    // Get tasks for this project
                    var projTasks = tasks.Where(t => GetProjectFile(t) == projFile).ToList();
                    var taskTimeMs = projTasks.Sum(t => GetDurationMs(t.StartTime, t.EndTime));

                    // Compiler time
                    var compilerTimeMs = projTasks
                        .Where(t => t.Name.Equals("Csc", StringComparison.OrdinalIgnoreCase) ||
                                   t.Name.Equals("Vbc", StringComparison.OrdinalIgnoreCase) ||
                                   t.Name.Equals("Fsc", StringComparison.OrdinalIgnoreCase))
                        .Sum(t => GetDurationMs(t.StartTime, t.EndTime));

                    // Copy time
                    var copyTimeMs = projTasks
                        .Where(t => t.Name.Equals("Copy", StringComparison.OrdinalIgnoreCase))
                        .Sum(t => GetDurationMs(t.StartTime, t.EndTime));

                    // Slowest targets for this project
                    var slowestTargets = includeTargetDetails
                        ? projTargets
                            .OrderByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                            .Take(5)
                            .Select(t => new
                            {
                                name = t.Name,
                                durationMs = Math.Round(GetDurationMs(t.StartTime, t.EndTime), 1)
                            })
                            .ToList()
                        : null;

                    return new
                    {
                        project = projName,
                        projectFile = Path.GetFileName(projFile),
                        wallClockMs = Math.Round(wallClockMs, 1),
                        targetTimeMs = Math.Round(targetTimeMs, 1),
                        compilerTimeMs = Math.Round(compilerTimeMs, 1),
                        copyTimeMs = Math.Round(copyTimeMs, 1),
                        targetCount = projTargets.Count,
                        taskCount = projTasks.Count,
                        percentOfBuild = buildDuration > 0 ? Math.Round(wallClockMs / buildDuration * 100, 1) : 0,
                        startOffset = Math.Round(GetDurationMs(buildStart, projStart), 1),
                        slowestTargets
                    };
                })
                .OrderByDescending(p => p.wallClockMs)
                .Take(limit)
                .ToList();

            // Summary statistics
            var totalProjectTime = projectData.Sum(p => p.wallClockMs);
            var totalCompilerTime = projectData.Sum(p => p.compilerTimeMs);
            var totalCopyTime = projectData.Sum(p => p.copyTimeMs);

            // Find potential bottlenecks
            var bottlenecks = new List<string>();
            var topProject = projectData.FirstOrDefault();
            if (topProject != null && topProject.percentOfBuild > 40)
            {
                bottlenecks.Add($"Project '{topProject.project}' takes {topProject.percentOfBuild}% of build time");
            }
            if (totalCompilerTime > buildDuration * 0.5)
            {
                bottlenecks.Add($"Compilation takes {Math.Round(totalCompilerTime / buildDuration * 100)}% of build time");
            }
            if (totalCopyTime > buildDuration * 0.2)
            {
                bottlenecks.Add($"File copying takes {Math.Round(totalCopyTime / buildDuration * 100)}% of build time");
            }

            return new
            {
                file = binlogPath,
                summary = new
                {
                    buildDurationMs = Math.Round(buildDuration, 1),
                    buildDurationFormatted = FormatDuration(TimeSpan.FromMilliseconds(buildDuration)),
                    projectCount = projectData.Count,
                    totalProjectTimeMs = Math.Round(totalProjectTime, 1),
                    totalCompilerTimeMs = Math.Round(totalCompilerTime, 1),
                    totalCopyTimeMs = Math.Round(totalCopyTime, 1)
                },
                bottlenecks = bottlenecks.Count > 0 ? bottlenecks : null,
                projects = projectData
            };
        });
    }

    [McpServerTool, Description("Focused performance comparison between two builds - highlights timing regressions and improvements")]
    public static string ComparePerformance(
        [Description("Path to the baseline (older/faster) binlog file")] string baselinePath,
        [Description("Path to the comparison (newer/slower) binlog file")] string comparisonPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Minimum timing change in ms to report (default: 100)")] double minChangeMs = 100)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogComparisonWithFormat(baselinePath, comparisonPath, outputFormat, "Performance Comparison", (baseline, comparison) =>
        {
            // Collect timing data from both builds
            var baselineData = CollectPerformanceData(baseline);
            var comparisonData = CollectPerformanceData(comparison);

            var baselineDurationMs = GetDurationMs(baseline.StartTime, baseline.EndTime);
            var comparisonDurationMs = GetDurationMs(comparison.StartTime, comparison.EndTime);
            var overallChangeMs = comparisonDurationMs - baselineDurationMs;
            var overallChangePercent = baselineDurationMs > 0 ? (overallChangeMs / baselineDurationMs) * 100 : 0;

            // Compare project timing
            var projectChanges = new List<object>();
            var allProjects = baselineData.ProjectTiming.Keys.Union(comparisonData.ProjectTiming.Keys);

            foreach (var project in allProjects)
            {
                var baseMs = baselineData.ProjectTiming.GetValueOrDefault(project, 0);
                var compMs = comparisonData.ProjectTiming.GetValueOrDefault(project, 0);
                var changeMs = compMs - baseMs;

                if (Math.Abs(changeMs) >= minChangeMs)
                {
                    projectChanges.Add(new
                    {
                        project,
                        baselineMs = Math.Round(baseMs, 1),
                        comparisonMs = Math.Round(compMs, 1),
                        changeMs = Math.Round(changeMs, 1),
                        changePercent = baseMs > 0 ? Math.Round(changeMs / baseMs * 100, 1) : (compMs > 0 ? 100.0 : 0),
                        status = changeMs > 0 ? "slower" : "faster"
                    });
                }
            }

            // Compare target timing
            var targetChanges = new List<object>();
            var allTargets = baselineData.TargetTiming.Keys.Union(comparisonData.TargetTiming.Keys);

            foreach (var target in allTargets)
            {
                var baseMs = baselineData.TargetTiming.GetValueOrDefault(target, 0);
                var compMs = comparisonData.TargetTiming.GetValueOrDefault(target, 0);
                var changeMs = compMs - baseMs;

                if (Math.Abs(changeMs) >= minChangeMs)
                {
                    targetChanges.Add(new
                    {
                        target,
                        baselineMs = Math.Round(baseMs, 1),
                        comparisonMs = Math.Round(compMs, 1),
                        changeMs = Math.Round(changeMs, 1),
                        changePercent = baseMs > 0 ? Math.Round(changeMs / baseMs * 100, 1) : (compMs > 0 ? 100.0 : 0),
                        status = changeMs > 0 ? "slower" : "faster"
                    });
                }
            }

            // Compare task type timing
            var taskTypeChanges = new List<object>();
            var allTaskTypes = baselineData.TaskTypeTiming.Keys.Union(comparisonData.TaskTypeTiming.Keys);

            foreach (var taskType in allTaskTypes)
            {
                var baseMs = baselineData.TaskTypeTiming.GetValueOrDefault(taskType, 0);
                var compMs = comparisonData.TaskTypeTiming.GetValueOrDefault(taskType, 0);
                var changeMs = compMs - baseMs;

                if (Math.Abs(changeMs) >= minChangeMs)
                {
                    taskTypeChanges.Add(new
                    {
                        taskType,
                        baselineMs = Math.Round(baseMs, 1),
                        comparisonMs = Math.Round(compMs, 1),
                        changeMs = Math.Round(changeMs, 1),
                        changePercent = baseMs > 0 ? Math.Round(changeMs / baseMs * 100, 1) : (compMs > 0 ? 100.0 : 0),
                        status = changeMs > 0 ? "slower" : "faster"
                    });
                }
            }

            // Sort by absolute change magnitude
            var sortedProjectChanges = projectChanges.Cast<dynamic>()
                .OrderByDescending(p => Math.Abs((double)p.changeMs))
                .Take(20)
                .ToList();

            var sortedTargetChanges = targetChanges.Cast<dynamic>()
                .OrderByDescending(t => Math.Abs((double)t.changeMs))
                .Take(30)
                .ToList();

            var sortedTaskTypeChanges = taskTypeChanges.Cast<dynamic>()
                .OrderByDescending(t => Math.Abs((double)t.changeMs))
                .Take(15)
                .ToList();

            // Identify regressions vs improvements
            var regressions = sortedProjectChanges.Where(p => (double)p.changeMs > 0).ToList();
            var improvements = sortedProjectChanges.Where(p => (double)p.changeMs < 0).ToList();

            // Parallelism comparison
            var baselineParallel = baselineData.MaxParallelProjects;
            var comparisonParallel = comparisonData.MaxParallelProjects;

            return new
            {
                baselineFile = baselinePath,
                comparisonFile = comparisonPath,
                minChangeMs,
                summary = new
                {
                    baselineDurationMs = Math.Round(baselineDurationMs, 1),
                    comparisonDurationMs = Math.Round(comparisonDurationMs, 1),
                    overallChangeMs = Math.Round(overallChangeMs, 1),
                    overallChangePercent = Math.Round(overallChangePercent, 1),
                    overallChangeFormatted = FormatDurationChange(overallChangeMs),
                    verdict = overallChangeMs > 500 ? "SLOWER" : (overallChangeMs < -500 ? "FASTER" : "SIMILAR"),
                    baselineParallelism = baselineParallel,
                    comparisonParallelism = comparisonParallel
                },
                projectRegressions = regressions.Count > 0 ? regressions : null,
                projectImprovements = improvements.Count > 0 ? improvements : null,
                targetChanges = sortedTargetChanges.Count > 0 ? sortedTargetChanges : null,
                taskTypeChanges = sortedTaskTypeChanges.Count > 0 ? sortedTaskTypeChanges : null
            };
        });
    }

    private static PerformanceData CollectPerformanceData(Build build)
    {
        var data = new PerformanceData();
        var projects = new List<Project>();
        var targets = new List<Target>();
        var tasks = new List<MSBuildTask>();

        build.VisitAllChildren<BaseNode>(node =>
        {
            switch (node)
            {
                case Project p:
                    projects.Add(p);
                    break;
                case Target t:
                    targets.Add(t);
                    break;
                case MSBuildTask task:
                    tasks.Add(task);
                    break;
            }
        });

        // Project timing
        foreach (var group in projects.GroupBy(p => p.Name))
        {
            var totalMs = group.Sum(p => GetDurationMs(p.StartTime, p.EndTime));
            data.ProjectTiming[group.Key ?? "Unknown"] = totalMs;
        }

        // Target timing (aggregated by name)
        foreach (var group in targets.GroupBy(t => t.Name))
        {
            var totalMs = group.Sum(t => GetDurationMs(t.StartTime, t.EndTime));
            data.TargetTiming[group.Key] = totalMs;
        }

        // Task type timing
        foreach (var group in tasks.GroupBy(t => t.Name))
        {
            var totalMs = group.Sum(t => GetDurationMs(t.StartTime, t.EndTime));
            data.TaskTypeTiming[group.Key] = totalMs;
        }

        // Calculate max parallelism
        var uniqueProjects = projects.GroupBy(p => p.ProjectFile).ToList();
        var buildStart = build.StartTime;
        var buildEnd = build.EndTime;
        var buildDuration = GetDurationMs(buildStart, buildEnd);

        if (buildDuration > 0)
        {
            var sampleCount = 20;
            var intervalMs = buildDuration / sampleCount;
            var maxParallel = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                var sampleTime = buildStart.AddMilliseconds(i * intervalMs);
                var activeProjects = uniqueProjects.Count(g =>
                    g.Min(p => p.StartTime) <= sampleTime &&
                    g.Max(p => p.EndTime) >= sampleTime);
                maxParallel = Math.Max(maxParallel, activeProjects);
            }

            data.MaxParallelProjects = maxParallel;
        }

        return data;
    }

    private static string FormatDurationChange(double changeMs)
    {
        var sign = changeMs >= 0 ? "+" : "";
        return $"{sign}{FormatDuration(TimeSpan.FromMilliseconds(Math.Abs(changeMs)))}";
    }

    private class PerformanceData
    {
        public Dictionary<string, double> ProjectTiming { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TargetTiming { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> TaskTypeTiming { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int MaxParallelProjects { get; set; } = 1;
    }

    [McpServerTool, Description("Identifies what's blocking parallelism - finds serialization points, long-running targets that block others, and dependency bottlenecks")]
    public static string GetParallelismBlockers(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown")] string format = "json",
        [Description("Minimum duration in ms for a target to be considered a blocker (default: 500)")] double minBlockerDurationMs = 500)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Parallelism Blockers", build =>
        {
            var projects = new List<Project>();
            var targets = new List<Target>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Project p:
                        projects.Add(p);
                        break;
                    case Target t:
                        targets.Add(t);
                        break;
                }
            });

            var buildStart = build.StartTime;
            var buildEnd = build.EndTime;
            var buildDuration = GetDurationMs(buildStart, buildEnd);

            // Get unique projects with their time ranges
            var projectRanges = projects
                .GroupBy(p => p.ProjectFile)
                .Select(g => new
                {
                    Name = g.First().Name,
                    File = g.Key,
                    Start = g.Min(p => p.StartTime),
                    End = g.Max(p => p.EndTime),
                    Duration = GetDurationMs(g.Min(p => p.StartTime), g.Max(p => p.EndTime))
                })
                .ToList();

            // Find serialization points - times when only one project was running
            var sampleCount = 100;
            var intervalMs = buildDuration / sampleCount;
            var serializationPeriods = new List<(DateTime start, DateTime end, string project)>();
            DateTime? serializationStart = null;
            string? serializationProject = null;

            for (int i = 0; i < sampleCount; i++)
            {
                var sampleTime = buildStart.AddMilliseconds(i * intervalMs);
                var activeProjects = projectRanges.Where(p =>
                    p.Start <= sampleTime && p.End >= sampleTime).ToList();

                if (activeProjects.Count == 1)
                {
                    if (serializationStart == null)
                    {
                        serializationStart = sampleTime;
                        serializationProject = activeProjects[0].Name;
                    }
                }
                else if (serializationStart != null)
                {
                    serializationPeriods.Add((serializationStart.Value, sampleTime, serializationProject!));
                    serializationStart = null;
                    serializationProject = null;
                }
            }

            // Close any open serialization period
            if (serializationStart != null)
            {
                serializationPeriods.Add((serializationStart.Value, buildEnd, serializationProject!));
            }

            // Group serialization periods by project
            var serializationByProject = serializationPeriods
                .GroupBy(p => p.project)
                .Select(g => new
                {
                    project = g.Key,
                    serializationTimeMs = Math.Round(g.Sum(p => GetDurationMs(p.start, p.end)), 1),
                    periods = g.Count()
                })
                .OrderByDescending(p => p.serializationTimeMs)
                .Take(10)
                .ToList();

            // Find long-running targets that block others
            var targetBlockers = targets
                .Where(t => GetDurationMs(t.StartTime, t.EndTime) >= minBlockerDurationMs)
                .Select(t =>
                {
                    var targetStart = t.StartTime;
                    var targetEnd = t.EndTime;
                    var targetDuration = GetDurationMs(targetStart, targetEnd);

                    // Count how many other projects were waiting during this target
                    var waitingProjects = projectRanges.Count(p =>
                        p.Start > targetStart && p.Start < targetEnd);

                    // Calculate overlap with other projects
                    var overlappingProjects = projectRanges.Count(p =>
                        p.File != GetProjectFile(t) &&
                        p.Start < targetEnd && p.End > targetStart);

                    return new
                    {
                        target = t.Name,
                        project = t.Project?.Name,
                        durationMs = Math.Round(targetDuration, 1),
                        waitingProjects,
                        overlappingProjects,
                        isSerializationPoint = overlappingProjects == 0,
                        startOffset = Math.Round(GetDurationMs(buildStart, targetStart), 1)
                    };
                })
                .Where(t => t.isSerializationPoint || t.waitingProjects > 0)
                .OrderByDescending(t => t.durationMs)
                .Take(20)
                .ToList();

            // Find projects that ran sequentially (no overlap)
            var sequentialProjects = projectRanges
                .Select(p =>
                {
                    var overlapping = projectRanges.Count(other =>
                        other.File != p.File &&
                        other.Start < p.End && other.End > p.Start);
                    return new
                    {
                        project = p.Name,
                        durationMs = Math.Round(p.Duration, 1),
                        overlappingProjects = overlapping,
                        isSequential = overlapping == 0
                    };
                })
                .Where(p => p.isSequential && p.durationMs >= minBlockerDurationMs)
                .OrderByDescending(p => p.durationMs)
                .ToList();

            // Calculate total serialization time
            var totalSerializationTime = serializationByProject.Sum(p => p.serializationTimeMs);
            var serializationPercent = buildDuration > 0 ? Math.Round(totalSerializationTime / buildDuration * 100, 1) : 0;

            // Recommendations
            var recommendations = new List<string>();
            if (serializationPercent > 30)
            {
                recommendations.Add($"Build spends {serializationPercent}% of time in serialized execution. Consider improving dependency structure.");
            }
            if (sequentialProjects.Count > 0)
            {
                var topSequential = sequentialProjects.First();
                recommendations.Add($"Project '{topSequential.project}' ({topSequential.durationMs}ms) runs without any parallel work. Consider dependency restructuring.");
            }
            var topBlocker = targetBlockers.FirstOrDefault();
            if (topBlocker != null && topBlocker.durationMs > buildDuration * 0.2)
            {
                recommendations.Add($"Target '{topBlocker.target}' in '{topBlocker.project}' takes {Math.Round(topBlocker.durationMs / buildDuration * 100)}% of build. Consider splitting or optimizing.");
            }

            return new
            {
                file = binlogPath,
                minBlockerDurationMs,
                summary = new
                {
                    buildDurationMs = Math.Round(buildDuration, 1),
                    projectCount = projectRanges.Count,
                    totalSerializationTimeMs = Math.Round(totalSerializationTime, 1),
                    serializationPercent,
                    sequentialProjectCount = sequentialProjects.Count,
                    targetBlockerCount = targetBlockers.Count
                },
                recommendations = recommendations.Count > 0 ? recommendations : null,
                serializationByProject = serializationByProject.Count > 0 ? serializationByProject : null,
                sequentialProjects = sequentialProjects.Count > 0 ? sequentialProjects : null,
                targetBlockers = targetBlockers.Count > 0 ? targetBlockers : null
            };
        });
    }

    [McpServerTool, Description("Deep dive into a single target's performance - shows all tasks, parameters, I/O operations, and timing breakdown")]
    public static string AnalyzeTarget(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Name of the target to analyze")] string targetName,
        [Description("Output format: json (default), markdown")] string format = "json",
        [Description("Filter to specific project (optional)")] string? projectFilter = null)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, $"Target Analysis: {targetName}", build =>
        {
            var matchingTargets = new List<Target>();

            build.VisitAllChildren<Target>(t =>
            {
                if (t.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    var projectName = t.Project?.Name;
                    if (string.IsNullOrEmpty(projectFilter) ||
                        (projectName?.Contains(projectFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    {
                        matchingTargets.Add(t);
                    }
                }
            });

            if (matchingTargets.Count == 0)
            {
                return new
                {
                    file = binlogPath,
                    targetName,
                    projectFilter,
                    error = $"Target '{targetName}' not found" + (projectFilter != null ? $" in project '{projectFilter}'" : "")
                };
            }

            var buildDuration = GetDurationMs(build.StartTime, build.EndTime);

            // Analyze each execution of the target
            var executions = matchingTargets
                .OrderByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                .Select(t =>
                {
                    var targetDuration = GetDurationMs(t.StartTime, t.EndTime);

                    // Get all tasks within this target
                    var tasks = new List<MSBuildTask>();
                    foreach (var child in t.Children)
                    {
                        if (child is MSBuildTask task)
                            tasks.Add(task);
                    }

                    // Analyze tasks
                    var taskAnalysis = tasks
                        .OrderByDescending(task => GetDurationMs(task.StartTime, task.EndTime))
                        .Select(task =>
                        {
                            var taskDuration = GetDurationMs(task.StartTime, task.EndTime);

                            // Extract key parameters
                            var parameters = new Dictionary<string, object>();
                            foreach (var child in task.Children)
                            {
                                if (child is Parameter param)
                                {
                                    var items = param.Children.OfType<Item>().ToList();
                                    if (items.Count == 1)
                                    {
                                        parameters[param.Name] = TruncateValue(items[0].Text, 100) ?? "";
                                    }
                                    else if (items.Count > 1)
                                    {
                                        parameters[param.Name] = new
                                        {
                                            count = items.Count,
                                            first = TruncateValue(items[0].Text, 80),
                                            last = items.Count > 1 ? TruncateValue(items[^1].Text, 80) : null
                                        };
                                    }
                                }
                            }

                            // Identify if it's an I/O task
                            var isIoTask = IoTaskNames.Contains(task.Name);

                            return new
                            {
                                task = task.Name,
                                durationMs = Math.Round(taskDuration, 1),
                                percentOfTarget = targetDuration > 0 ? Math.Round(taskDuration / targetDuration * 100, 1) : 0,
                                isIoTask,
                                parameters = parameters.Count > 0 ? parameters : null
                            };
                        })
                        .ToList();

                    // Summarize task types
                    var taskTypeSummary = tasks
                        .GroupBy(task => task.Name)
                        .Select(g => new
                        {
                            taskType = g.Key,
                            count = g.Count(),
                            totalMs = Math.Round(g.Sum(task => GetDurationMs(task.StartTime, task.EndTime)), 1)
                        })
                        .OrderByDescending(s => s.totalMs)
                        .ToList();

                    // Calculate I/O time
                    var ioTimeMs = tasks
                        .Where(task => IoTaskNames.Contains(task.Name))
                        .Sum(task => GetDurationMs(task.StartTime, task.EndTime));

                    return new
                    {
                        project = t.Project?.Name,
                        succeeded = t.Succeeded,
                        durationMs = Math.Round(targetDuration, 1),
                        durationFormatted = FormatDuration(TimeSpan.FromMilliseconds(targetDuration)),
                        percentOfBuild = buildDuration > 0 ? Math.Round(targetDuration / buildDuration * 100, 1) : 0,
                        startTime = t.StartTime.ToTimeString(),
                        endTime = t.EndTime.ToTimeString(),
                        taskCount = tasks.Count,
                        ioTimeMs = Math.Round(ioTimeMs, 1),
                        ioPercent = targetDuration > 0 ? Math.Round(ioTimeMs / targetDuration * 100, 1) : 0,
                        taskTypeSummary,
                        tasks = taskAnalysis.Take(20).ToList()
                    };
                })
                .ToList();

            // Aggregate statistics across all executions
            var totalDuration = executions.Sum(e => e.durationMs);
            var avgDuration = executions.Count > 0 ? totalDuration / executions.Count : 0;

            return new
            {
                file = binlogPath,
                targetName,
                projectFilter,
                summary = new
                {
                    executionCount = executions.Count,
                    totalDurationMs = Math.Round(totalDuration, 1),
                    avgDurationMs = Math.Round(avgDuration, 1),
                    maxDurationMs = executions.Count > 0 ? executions.Max(e => e.durationMs) : 0,
                    percentOfBuild = buildDuration > 0 ? Math.Round(totalDuration / buildDuration * 100, 1) : 0
                },
                executions
            };
        });
    }
}
