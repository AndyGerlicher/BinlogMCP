using BinlogMcp.Formatting;
using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace BinlogMcp.Tools;

public static partial class BinlogTools
{
    [McpServerTool, Description("Gets environment variables that were set during the build. Useful for diagnosing build behavior differences between machines.")]
    public static string GetEnvironmentVariables(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Filter environment variable names (case-insensitive contains)")] string? filter = null,
        [Description("Show only MSBuild/DOTNET/COMPLUS related variables")] bool msbuildOnly = false,
        [Description("Output format: json (default), markdown, csv")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Environment Variables", build =>
        {
            var envVars = new List<object>();

            // Environment variables are stored in Build.EnvironmentFolder as Property children
            // or can be found via Build's direct children
            var envFolder = build.Children
                .OfType<Folder>()
                .FirstOrDefault(f => f.Name?.Contains("Environment", StringComparison.OrdinalIgnoreCase) == true);

            if (envFolder != null)
            {
                foreach (var child in envFolder.Children)
                {
                    if (child is Property prop)
                    {
                        AddEnvVar(envVars, prop.Name, prop.Value, filter, msbuildOnly);
                    }
                    else if (child is NameValueNode nvn)
                    {
                        AddEnvVar(envVars, nvn.Name, nvn.Value, filter, msbuildOnly);
                    }
                }
            }

            // Also check for environment properties that might be elsewhere
            // Some binlogs store env vars as properties with specific patterns
            if (envVars.Count == 0)
            {
                // Fallback: look for properties that look like environment variables
                var properties = new List<Property>();
                build.VisitAllChildren<Property>(p => properties.Add(p));

                var envPatterns = new[] { "PATH", "TEMP", "TMP", "HOME", "USER", "MSBUILD", "DOTNET", "COMPLUS", "NUGET" };
                foreach (var prop in properties.DistinctBy(p => p.Name?.ToUpperInvariant()))
                {
                    if (prop.Name == null) continue;
                    var upper = prop.Name.ToUpperInvariant();
                    // Check if starts with known env pattern or is ALL_CAPS style
                    var matchesPattern = envPatterns.Any(p => upper.StartsWith(p));
                    var isAllCaps = prop.Name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_');

                    if (matchesPattern || isAllCaps)
                    {
                        AddEnvVar(envVars, prop.Name, prop.Value, filter, msbuildOnly);
                    }
                }
            }

            // Sort by name
            var sorted = envVars
                .Cast<dynamic>()
                .OrderBy(e => (string)e.name)
                .ToList();

            // Categorize
            var categories = sorted
                .GroupBy(e => CategorizeEnvVar((string)e.name))
                .ToDictionary(g => g.Key, g => g.Count());

            return new
            {
                file = binlogPath,
                filter,
                msbuildOnly,
                count = sorted.Count,
                categories,
                variables = sorted
            };
        });
    }

    private static void AddEnvVar(List<object> envVars, string? name, string? value, string? filter, bool msbuildOnly)
    {
        if (string.IsNullOrEmpty(name)) return;

        // Apply msbuildOnly filter
        if (msbuildOnly)
        {
            var upper = name.ToUpperInvariant();
            if (!upper.StartsWith("MSBUILD") && !upper.StartsWith("DOTNET") && !upper.StartsWith("COMPLUS"))
                return;
        }

        // Apply name filter
        if (name.FailsFilter(filter)) return;

        // Check for duplicates
        if (envVars.Any(e => ((dynamic)e).name == name)) return;

        envVars.Add(new
        {
            name,
            value = TruncateValue(value, 500),
            category = CategorizeEnvVar(name)
        });
    }

    private static string CategorizeEnvVar(string name)
    {
        var upper = name.ToUpperInvariant();
        return upper switch
        {
            _ when upper.StartsWith("MSBUILD") => "MSBuild",
            _ when upper.StartsWith("DOTNET") => ".NET",
            _ when upper.StartsWith("COMPLUS") => "CLR",
            _ when upper.StartsWith("NUGET") => "NuGet",
            _ when upper.StartsWith("VS") || upper.Contains("VISUAL") => "Visual Studio",
            _ when upper == "PATH" || upper == "PATHEXT" => "System Path",
            _ when upper.Contains("TEMP") || upper.Contains("TMP") => "Temp Directories",
            _ when upper.Contains("PROGRAM") || upper.Contains("APPDATA") || upper.Contains("HOME") => "System Directories",
            _ => "Other"
        };
    }

    [McpServerTool, Description("Exports timeline data for external visualization tools. Provides hierarchical timing data for projects, targets, and tasks.")]
    public static string GetTimeline(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Level of detail: 'projects', 'targets', 'tasks' (default: targets)")] string level = "targets",
        [Description("Minimum duration in milliseconds to include (default: 0)")] double minDurationMs = 0,
        [Description("Filter by project name (case-insensitive contains)")] string? projectFilter = null)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var showTasks = level.Equals("tasks", StringComparison.OrdinalIgnoreCase);
            var showTargets = showTasks || level.Equals("targets", StringComparison.OrdinalIgnoreCase);

            var events = new List<TimelineEvent>();
            var buildStart = build.StartTime;

            // Collect projects
            var projects = new List<Project>();
            build.VisitAllChildren<Project>(p => projects.Add(p));

            foreach (var project in projects.DistinctBy(p => p.Name + p.TargetFramework))
            {
                if (project.Name.FailsFilter(projectFilter)) continue;

                var projectDuration = GetDurationMs(project.StartTime, project.EndTime);
                if (projectDuration < minDurationMs) continue;

                // Check if project has errors
                var hasErrors = false;
                project.VisitAllChildren<Error>(e => hasErrors = true);

                var projectEvent = new TimelineEvent
                {
                    Id = $"project_{project.Name}_{project.TargetFramework}",
                    Name = project.Name ?? "Unknown",
                    Type = "project",
                    StartMs = (project.StartTime - buildStart).TotalMilliseconds,
                    DurationMs = projectDuration,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["targetFramework"] = project.TargetFramework,
                        ["succeeded"] = !hasErrors
                    }
                };

                if (showTargets)
                {
                    projectEvent.Children = new List<TimelineEvent>();

                    foreach (var target in project.Children.OfType<Target>())
                    {
                        var targetDuration = GetDurationMs(target.StartTime, target.EndTime);
                        if (targetDuration < minDurationMs) continue;

                        var targetEvent = new TimelineEvent
                        {
                            Id = $"target_{project.Name}_{target.Name}",
                            Name = target.Name ?? "Unknown",
                            Type = "target",
                            StartMs = (target.StartTime - buildStart).TotalMilliseconds,
                            DurationMs = targetDuration,
                            Metadata = new Dictionary<string, object?>
                            {
                                ["succeeded"] = target.Succeeded
                            }
                        };

                        if (showTasks)
                        {
                            targetEvent.Children = new List<TimelineEvent>();

                            foreach (var task in target.Children.OfType<Microsoft.Build.Logging.StructuredLogger.Task>())
                            {
                                var taskDuration = GetDurationMs(task.StartTime, task.EndTime);
                                if (taskDuration < minDurationMs) continue;

                                targetEvent.Children.Add(new TimelineEvent
                                {
                                    Id = $"task_{project.Name}_{target.Name}_{task.Name}",
                                    Name = task.Name ?? "Unknown",
                                    Type = "task",
                                    StartMs = (task.StartTime - buildStart).TotalMilliseconds,
                                    DurationMs = taskDuration,
                                    Metadata = new Dictionary<string, object?>
                                    {
                                        ["succeeded"] = !task.Children.OfType<Error>().Any()
                                    }
                                });
                            }
                        }

                        projectEvent.Children.Add(targetEvent);
                    }
                }

                events.Add(projectEvent);
            }

            // Sort by start time
            events = events.OrderBy(e => e.StartMs).ToList();

            return new
            {
                file = binlogPath,
                level,
                minDurationMs,
                buildStartTime = buildStart.ToString("o"),
                buildDurationMs = GetDurationMs(build.StartTime, build.EndTime),
                eventCount = CountEvents(events),
                events
            };
        });
    }

    private static int CountEvents(List<TimelineEvent> events)
    {
        var count = events.Count;
        foreach (var e in events)
        {
            if (e.Children != null)
                count += CountEvents(e.Children);
        }
        return count;
    }

    private class TimelineEvent
    {
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Type { get; set; }
        public double StartMs { get; set; }
        public double DurationMs { get; set; }
        public Dictionary<string, object?>? Metadata { get; set; }
        public List<TimelineEvent>? Children { get; set; }
    }
}
