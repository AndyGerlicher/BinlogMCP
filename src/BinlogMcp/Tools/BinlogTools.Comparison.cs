using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace BinlogMcp.Tools;

public static partial class BinlogTools
{
    [McpServerTool, Description("Compares two binlog files to show what changed between builds (timing, errors, warnings, targets)")]
    public static string CompareBinlogs(
        [Description("Path to the baseline (older) binlog file")] string baselinePath,
        [Description("Path to the comparison (newer) binlog file")] string comparisonPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogComparisonWithFormat(baselinePath, comparisonPath, outputFormat, "Build Comparison", (baseline, comparison) =>
        {
            // Collect data from both builds
            var baselineData = CollectBuildData(baseline);
            var comparisonData = CollectBuildData(comparison);

            var baselineDurationMs = GetDurationMs(baseline.StartTime, baseline.EndTime);
            var comparisonDurationMs = GetDurationMs(comparison.StartTime, comparison.EndTime);

            // Compare build results
            var buildComparison = new
            {
                baseline = new
                {
                    succeeded = baseline.Succeeded,
                    duration = FormatDuration(GetDuration(baseline.StartTime, baseline.EndTime)),
                    durationMs = baselineDurationMs,
                    errorCount = baselineData.Errors.Count,
                    warningCount = baselineData.Warnings.Count
                },
                comparison = new
                {
                    succeeded = comparison.Succeeded,
                    duration = FormatDuration(GetDuration(comparison.StartTime, comparison.EndTime)),
                    durationMs = comparisonDurationMs,
                    errorCount = comparisonData.Errors.Count,
                    warningCount = comparisonData.Warnings.Count
                },
                durationChange = FormatDurationChange(baselineDurationMs, comparisonDurationMs)
            };

            // Compare errors
            var baselineErrorKeys = baselineData.Errors.Select(e => $"{e.Code}:{e.File}:{e.LineNumber}").ToHashSet();
            var comparisonErrorKeys = comparisonData.Errors.Select(e => $"{e.Code}:{e.File}:{e.LineNumber}").ToHashSet();

            var newErrors = comparisonData.Errors
                .Where(e => !baselineErrorKeys.Contains($"{e.Code}:{e.File}:{e.LineNumber}"))
                .Select(e => new { e.Code, e.File, e.LineNumber, message = e.Text })
                .ToList();

            var fixedErrors = baselineData.Errors
                .Where(e => !comparisonErrorKeys.Contains($"{e.Code}:{e.File}:{e.LineNumber}"))
                .Select(e => new { e.Code, e.File, e.LineNumber, message = e.Text })
                .ToList();

            // Compare warnings
            var baselineWarningKeys = baselineData.Warnings.Select(w => $"{w.Code}:{w.File}:{w.LineNumber}").ToHashSet();
            var comparisonWarningKeys = comparisonData.Warnings.Select(w => $"{w.Code}:{w.File}:{w.LineNumber}").ToHashSet();

            var newWarnings = comparisonData.Warnings
                .Where(w => !baselineWarningKeys.Contains($"{w.Code}:{w.File}:{w.LineNumber}"))
                .Select(w => new { w.Code, w.File, w.LineNumber, message = w.Text })
                .Take(20)
                .ToList();

            var fixedWarnings = baselineData.Warnings
                .Where(w => !comparisonWarningKeys.Contains($"{w.Code}:{w.File}:{w.LineNumber}"))
                .Select(w => new { w.Code, w.File, w.LineNumber, message = w.Text })
                .Take(20)
                .ToList();

            // Compare target timing (top slowdowns and speedups)
            var baselineTargetTimes = baselineData.Targets
                .GroupBy(t => t.Name)
                .ToDictionary(g => g.Key, g => g.Sum(t => GetDurationMs(t.StartTime, t.EndTime)));

            var comparisonTargetTimes = comparisonData.Targets
                .GroupBy(t => t.Name)
                .ToDictionary(g => g.Key, g => g.Sum(t => GetDurationMs(t.StartTime, t.EndTime)));

            var targetChanges = comparisonTargetTimes.Keys
                .Union(baselineTargetTimes.Keys)
                .Select(name =>
                {
                    var baseMs = baselineTargetTimes.GetValueOrDefault(name, 0);
                    var compMs = comparisonTargetTimes.GetValueOrDefault(name, 0);
                    var changeMs = compMs - baseMs;
                    return new
                    {
                        name,
                        baselineMs = baseMs,
                        comparisonMs = compMs,
                        changeMs,
                        changePercent = baseMs > 0 ? Math.Round((changeMs / baseMs) * 100, 1) : (compMs > 0 ? 100.0 : 0)
                    };
                })
                .Where(t => Math.Abs(t.changeMs) > 100) // Only show changes > 100ms
                .OrderByDescending(t => Math.Abs(t.changeMs))
                .Take(15)
                .ToList();

            // Analyze build types (clean vs incremental)
            var baselineAnalysis = AnalyzeBuildType(baselineData, baselineDurationMs);
            var comparisonAnalysis = AnalyzeBuildType(comparisonData, comparisonDurationMs);

            var conclusion = GenerateComparisonConclusion(
                baselineAnalysis,
                comparisonAnalysis,
                baselineDurationMs,
                comparisonDurationMs,
                baseline.Succeeded,
                comparison.Succeeded);

            return new
            {
                baselineFile = baselinePath,
                comparisonFile = comparisonPath,
                summary = buildComparison,
                buildTypeAnalysis = new
                {
                    baseline = new
                    {
                        totalTargets = baselineAnalysis.TotalTargets,
                        executedTargets = baselineAnalysis.ExecutedTargets,
                        skippedTargets = baselineAnalysis.SkippedTargets,
                        skippedPercent = baselineAnalysis.SkippedPercent,
                        upToDateMessages = baselineAnalysis.UpToDateMessageCount,
                        compilationTargetsExecuted = baselineAnalysis.CompilationTargetsExecuted,
                        isLikelyCleanBuild = baselineAnalysis.IsLikelyClean,
                        isLikelyIncrementalBuild = baselineAnalysis.IsLikelyIncremental
                    },
                    comparison = new
                    {
                        totalTargets = comparisonAnalysis.TotalTargets,
                        executedTargets = comparisonAnalysis.ExecutedTargets,
                        skippedTargets = comparisonAnalysis.SkippedTargets,
                        skippedPercent = comparisonAnalysis.SkippedPercent,
                        upToDateMessages = comparisonAnalysis.UpToDateMessageCount,
                        compilationTargetsExecuted = comparisonAnalysis.CompilationTargetsExecuted,
                        isLikelyCleanBuild = comparisonAnalysis.IsLikelyClean,
                        isLikelyIncrementalBuild = comparisonAnalysis.IsLikelyIncremental
                    }
                },
                errors = new
                {
                    newCount = newErrors.Count,
                    fixedCount = fixedErrors.Count,
                    newErrors,
                    fixedErrors
                },
                warnings = new
                {
                    newCount = comparisonData.Warnings.Count - baselineData.Warnings.Count + fixedWarnings.Count,
                    fixedCount = fixedWarnings.Count,
                    newWarnings,
                    fixedWarnings
                },
                targetTimingChanges = targetChanges,
                conclusion
            };
        });
    }

    private static BuildData CollectBuildData(Build build)
    {
        var data = new BuildData();
        build.VisitAllChildren<BaseNode>(node =>
        {
            switch (node)
            {
                case Error e:
                    data.Errors.Add(e);
                    break;
                case Warning w:
                    data.Warnings.Add(w);
                    break;
                case Target t:
                    data.Targets.Add(t);
                    break;
                case Message m:
                    data.Messages.Add(m);
                    break;
            }
        });
        return data;
    }

    private static string FormatDurationChange(double baselineMs, double comparisonMs)
    {
        var changeMs = comparisonMs - baselineMs;
        var changePercent = baselineMs > 0 ? (changeMs / baselineMs) * 100 : 0;
        var sign = changeMs >= 0 ? "+" : "";
        return $"{sign}{FormatDuration(TimeSpan.FromMilliseconds(changeMs))} ({sign}{changePercent:0.#}%)";
    }

    private static BuildTypeAnalysis AnalyzeBuildType(BuildData data, double durationMs)
    {
        var executedTargets = data.Targets.Where(t => GetDurationMs(t.StartTime, t.EndTime) > 0).ToList();
        var skippedTargets = data.Targets.Where(t => GetDurationMs(t.StartTime, t.EndTime) == 0).ToList();

        var totalTargets = data.Targets.Count;
        var skippedPercent = totalTargets > 0 ? (double)skippedTargets.Count / totalTargets * 100 : 0;

        // Count up-to-date messages
        var upToDateCount = data.Messages.Count(m =>
            m.Text != null && (
                m.Text.Contains("Skipping target", StringComparison.OrdinalIgnoreCase) ||
                m.Text.Contains("up-to-date", StringComparison.OrdinalIgnoreCase) ||
                (m.Text.Contains("Building target", StringComparison.OrdinalIgnoreCase) &&
                 m.Text.Contains("completely", StringComparison.OrdinalIgnoreCase))));

        // Look at CoreCompile execution
        var coreCompileTargets = executedTargets.Where(t =>
            t.Name.Equals("CoreCompile", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Equals("Csc", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Equals("Vbc", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Equals("Fsc", StringComparison.OrdinalIgnoreCase)).ToList();

        // Calculate total execution time for executed targets
        var totalExecutionTimeMs = executedTargets.Sum(t => GetDurationMs(t.StartTime, t.EndTime));

        // Heuristics for build type
        // Note: These are hints, not definitive - comparison context is needed for best results
        // - High skip rate (>60%) suggests incremental
        // - Many up-to-date messages suggest incremental
        // - Having compilation targets suggests actual work was done
        var isLikelyIncremental = skippedPercent > 60 || upToDateCount > 50;
        var isLikelyClean = skippedPercent < 20 && coreCompileTargets.Count > 0;

        return new BuildTypeAnalysis
        {
            TotalTargets = totalTargets,
            ExecutedTargets = executedTargets.Count,
            SkippedTargets = skippedTargets.Count,
            SkippedPercent = Math.Round(skippedPercent, 1),
            UpToDateMessageCount = upToDateCount,
            CompilationTargetsExecuted = coreCompileTargets.Count,
            TotalExecutionTimeMs = totalExecutionTimeMs,
            IsLikelyIncremental = isLikelyIncremental,
            IsLikelyClean = isLikelyClean
        };
    }

    private static string GenerateComparisonConclusion(
        BuildTypeAnalysis baselineAnalysis,
        BuildTypeAnalysis comparisonAnalysis,
        double baselineDurationMs,
        double comparisonDurationMs,
        bool baselineSucceeded,
        bool comparisonSucceeded)
    {
        var durationChangePercent = baselineDurationMs > 0
            ? ((comparisonDurationMs - baselineDurationMs) / baselineDurationMs) * 100
            : 0;

        // Calculate execution time difference (actual work done, not wall clock)
        var execTimeChangePercent = baselineAnalysis.TotalExecutionTimeMs > 0
            ? ((comparisonAnalysis.TotalExecutionTimeMs - baselineAnalysis.TotalExecutionTimeMs) / baselineAnalysis.TotalExecutionTimeMs) * 100
            : 0;

        // Success/failure changes take priority
        if (baselineSucceeded && !comparisonSucceeded)
            return "BUILD REGRESSION: Comparison build failed while baseline succeeded. Check errors section.";

        if (!baselineSucceeded && comparisonSucceeded)
            return "BUILD FIXED: Comparison build succeeded while baseline failed.";

        // Large performance difference (>40% faster) strongly suggests incremental vs clean
        // even if both builds have similar target skip rates
        if (durationChangePercent < -40)
        {
            // Comparison is much faster - likely incremental (no-op or minimal work)
            if (baselineAnalysis.CompilationTargetsExecuted > 0 && comparisonAnalysis.CompilationTargetsExecuted > 0)
            {
                // Both compiled, but comparison was much faster
                return $"INCREMENTAL BUILD LIKELY: Comparison build is {Math.Abs(durationChangePercent):0.#}% faster than baseline. " +
                       $"Both builds executed {comparisonAnalysis.CompilationTargetsExecuted} compilation targets, but comparison completed " +
                       $"in {FormatDuration(TimeSpan.FromMilliseconds(comparisonDurationMs))} vs baseline's {FormatDuration(TimeSpan.FromMilliseconds(baselineDurationMs))}. " +
                       $"This pattern suggests the comparison build had cached outputs and performed minimal actual work.";
            }
            else if (baselineAnalysis.CompilationTargetsExecuted > 0)
            {
                // Baseline compiled, comparison didn't
                return $"INCREMENTAL BUILD DETECTED: Comparison build is {Math.Abs(durationChangePercent):0.#}% faster. " +
                       $"Baseline executed {baselineAnalysis.CompilationTargetsExecuted} compilation targets while comparison executed {comparisonAnalysis.CompilationTargetsExecuted}. " +
                       $"The comparison appears to be a no-op incremental build (all outputs up-to-date).";
            }
            else
            {
                return $"SIGNIFICANT SPEEDUP: Comparison build is {Math.Abs(durationChangePercent):0.#}% faster than baseline " +
                       $"({FormatDuration(TimeSpan.FromMilliseconds(comparisonDurationMs))} vs {FormatDuration(TimeSpan.FromMilliseconds(baselineDurationMs))}). " +
                       $"This suggests the comparison build had more cached/up-to-date outputs.";
            }
        }

        if (durationChangePercent > 40)
        {
            // Comparison is much slower - likely clean vs incremental
            return $"CLEAN BUILD LIKELY: Comparison build is {durationChangePercent:0.#}% slower than baseline. " +
                   $"This pattern suggests the comparison build performed a full rebuild while baseline may have been incremental.";
        }

        // Check for clean vs incremental based on heuristics
        if (baselineAnalysis.IsLikelyClean && comparisonAnalysis.IsLikelyIncremental)
        {
            return $"INCREMENTAL BUILD DETECTED: The comparison build appears to be an incremental build " +
                   $"(skipped {comparisonAnalysis.SkippedPercent:0.#}% of targets, {comparisonAnalysis.UpToDateMessageCount} up-to-date messages) " +
                   $"while baseline was likely a clean build (only {baselineAnalysis.SkippedPercent:0.#}% skipped). " +
                   $"The {Math.Abs(durationChangePercent):0.#}% faster build time is expected behavior for incremental builds.";
        }

        if (baselineAnalysis.IsLikelyIncremental && comparisonAnalysis.IsLikelyClean)
        {
            return $"CLEAN BUILD DETECTED: The comparison build appears to be a clean build " +
                   $"(only {comparisonAnalysis.SkippedPercent:0.#}% skipped) while baseline was likely incremental " +
                   $"({baselineAnalysis.SkippedPercent:0.#}% skipped). The longer build time is expected.";
        }

        // Both incremental
        if (baselineAnalysis.IsLikelyIncremental && comparisonAnalysis.IsLikelyIncremental)
        {
            if (Math.Abs(durationChangePercent) < 10)
                return "Both builds appear to be incremental builds with similar performance.";
            else if (durationChangePercent > 0)
                return $"Both builds are incremental. Comparison is {durationChangePercent:0.#}% slower - investigate targets that executed.";
            else
                return $"Both builds are incremental. Comparison is {Math.Abs(durationChangePercent):0.#}% faster.";
        }

        // Both clean builds
        if (baselineAnalysis.IsLikelyClean && comparisonAnalysis.IsLikelyClean)
        {
            if (Math.Abs(durationChangePercent) < 10)
                return "Both builds appear to be clean builds with similar performance.";
            else if (durationChangePercent > 20)
                return $"PERFORMANCE REGRESSION: Comparison build is {durationChangePercent:0.#}% slower. Review targetTimingChanges for bottlenecks.";
            else if (durationChangePercent < -20)
                return $"PERFORMANCE IMPROVEMENT: Comparison build is {Math.Abs(durationChangePercent):0.#}% faster.";
            else
                return $"Both builds are clean builds. Duration changed by {durationChangePercent:0.#}%.";
        }

        // Default
        if (durationChangePercent > 20)
            return $"Comparison build is {durationChangePercent:0.#}% slower than baseline.";
        else if (durationChangePercent < -20)
            return $"Comparison build is {Math.Abs(durationChangePercent):0.#}% faster than baseline.";
        else
            return "Builds have similar performance characteristics.";
    }

    private class BuildData
    {
        public List<Error> Errors { get; } = [];
        public List<Warning> Warnings { get; } = [];
        public List<Target> Targets { get; } = [];
        public List<Message> Messages { get; } = [];
    }

    private class BuildTypeAnalysis
    {
        public int TotalTargets { get; init; }
        public int ExecutedTargets { get; init; }
        public int SkippedTargets { get; init; }
        public double SkippedPercent { get; init; }
        public int UpToDateMessageCount { get; init; }
        public int CompilationTargetsExecuted { get; init; }
        public double TotalExecutionTimeMs { get; init; }
        public bool IsLikelyIncremental { get; init; }
        public bool IsLikelyClean { get; init; }
    }

    [McpServerTool, Description("Compares MSBuild property values between two builds - shows added, removed, and changed properties")]
    public static string DiffProperties(
        [Description("Path to the baseline (older) binlog file")] string baselinePath,
        [Description("Path to the comparison (newer) binlog file")] string comparisonPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Filter properties by name pattern (optional)")] string? nameFilter = null,
        [Description("Only show important/common properties like Configuration, Platform, OutputPath (default: false)")] bool importantOnly = false)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogComparisonWithFormat(baselinePath, comparisonPath, outputFormat, "Property Differences", (baseline, comparison) =>
        {
            var baselineProps = CollectProperties(baseline, nameFilter);
            var comparisonProps = CollectProperties(comparison, nameFilter);

            // Important property names to highlight
            var importantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Configuration", "Platform", "TargetFramework", "TargetFrameworks",
                "OutputPath", "OutputType", "AssemblyName", "RootNamespace",
                "Version", "AssemblyVersion", "FileVersion", "PackageVersion",
                "LangVersion", "Nullable", "ImplicitUsings", "TreatWarningsAsErrors",
                "MSBuildProjectDirectory", "MSBuildProjectFile", "RuntimeIdentifier"
            };

            // Filter to important only if requested
            if (importantOnly)
            {
                baselineProps = baselineProps
                    .Where(kvp => importantNames.Contains(kvp.Key.Split(':')[0]))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                comparisonProps = comparisonProps
                    .Where(kvp => importantNames.Contains(kvp.Key.Split(':')[0]))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            // Find differences
            var allKeys = baselineProps.Keys.Union(comparisonProps.Keys).ToHashSet();

            var added = new List<object>();
            var removed = new List<object>();
            var changed = new List<object>();
            var unchanged = 0;

            foreach (var key in allKeys)
            {
                var parts = key.Split(':', 2);
                var propName = parts[0];
                var project = parts.Length > 1 ? parts[1] : null;

                var hasBaseline = baselineProps.TryGetValue(key, out var baseValue);
                var hasComparison = comparisonProps.TryGetValue(key, out var compValue);

                if (hasBaseline && hasComparison)
                {
                    if (baseValue != compValue)
                    {
                        changed.Add(new
                        {
                            property = propName,
                            project,
                            baselineValue = TruncateValue(baseValue, 100),
                            comparisonValue = TruncateValue(compValue, 100),
                            isImportant = importantNames.Contains(propName)
                        });
                    }
                    else
                    {
                        unchanged++;
                    }
                }
                else if (hasComparison && !hasBaseline)
                {
                    added.Add(new
                    {
                        property = propName,
                        project,
                        value = TruncateValue(compValue, 100),
                        isImportant = importantNames.Contains(propName)
                    });
                }
                else if (hasBaseline && !hasComparison)
                {
                    removed.Add(new
                    {
                        property = propName,
                        project,
                        value = TruncateValue(baseValue, 100),
                        isImportant = importantNames.Contains(propName)
                    });
                }
            }

            // Sort important properties first
            var sortedChanged = changed.Cast<dynamic>()
                .OrderByDescending(c => (bool)c.isImportant)
                .ThenBy(c => (string)c.property)
                .Take(50)
                .ToList();

            var sortedAdded = added.Cast<dynamic>()
                .OrderByDescending(a => (bool)a.isImportant)
                .ThenBy(a => (string)a.property)
                .Take(30)
                .ToList();

            var sortedRemoved = removed.Cast<dynamic>()
                .OrderByDescending(r => (bool)r.isImportant)
                .ThenBy(r => (string)r.property)
                .Take(30)
                .ToList();

            // Find important changes specifically
            var importantChanges = changed.Cast<dynamic>()
                .Where(c => (bool)c.isImportant)
                .ToList();

            return new
            {
                baselineFile = baselinePath,
                comparisonFile = comparisonPath,
                nameFilter,
                importantOnly,
                summary = new
                {
                    totalBaselineProperties = baselineProps.Count,
                    totalComparisonProperties = comparisonProps.Count,
                    addedCount = added.Count,
                    removedCount = removed.Count,
                    changedCount = changed.Count,
                    unchangedCount = unchanged,
                    importantChangesCount = importantChanges.Count
                },
                importantChanges = importantChanges.Count > 0 ? importantChanges : null,
                changed = sortedChanged,
                added = sortedAdded,
                removed = sortedRemoved
            };
        });
    }

    private static Dictionary<string, string?> CollectProperties(Build build, string? nameFilter)
    {
        var props = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        build.VisitAllChildren<Property>(p =>
        {
            if (p.Name.FailsFilter(nameFilter))
                return;

            var project = GetProjectName(p) ?? "Global";
            var key = $"{p.Name}:{project}";

            // Keep the last value (final value wins)
            props[key] = p.Value;
        });

        return props;
    }

    [McpServerTool, Description("Compares MSBuild items between two builds - shows added, removed items (Compile, PackageReference, Reference, etc.)")]
    public static string DiffItems(
        [Description("Path to the baseline (older) binlog file")] string baselinePath,
        [Description("Path to the comparison (newer) binlog file")] string comparisonPath,
        [Description("Item type to compare (e.g., 'Compile', 'PackageReference', 'Reference'). If not specified, compares common types.")] string? itemType = null)
    {
        return ExecuteBinlogComparison(baselinePath, comparisonPath, (baseline, comparison) =>
        {
            // Default item types to compare
            var itemTypes = string.IsNullOrEmpty(itemType)
                ? new[] { "Compile", "PackageReference", "Reference", "ProjectReference", "Content", "None" }
                : new[] { itemType };

            var results = new List<object>();

            foreach (var type in itemTypes)
            {
                var baselineItems = CollectItems(baseline, type);
                var comparisonItems = CollectItems(comparison, type);

                var allKeys = baselineItems.Keys.Union(comparisonItems.Keys).ToHashSet();

                var added = new List<object>();
                var removed = new List<object>();
                var versionChanged = new List<object>();

                foreach (var key in allKeys)
                {
                    var hasBaseline = baselineItems.TryGetValue(key, out var baseInfo);
                    var hasComparison = comparisonItems.TryGetValue(key, out var compInfo);

                    if (hasBaseline && hasComparison)
                    {
                        // Check for version changes (mainly for PackageReference)
                        if (baseInfo.version != null && compInfo.version != null &&
                            baseInfo.version != compInfo.version)
                        {
                            versionChanged.Add(new
                            {
                                item = key.Split(':')[0],
                                project = key.Contains(':') ? key.Split(':')[1] : null,
                                baselineVersion = baseInfo.version,
                                comparisonVersion = compInfo.version
                            });
                        }
                    }
                    else if (hasComparison && !hasBaseline)
                    {
                        added.Add(new
                        {
                            item = key.Split(':')[0],
                            project = key.Contains(':') ? key.Split(':')[1] : null,
                            version = compInfo.version,
                            metadata = compInfo.metadata
                        });
                    }
                    else if (hasBaseline && !hasComparison)
                    {
                        removed.Add(new
                        {
                            item = key.Split(':')[0],
                            project = key.Contains(':') ? key.Split(':')[1] : null,
                            version = baseInfo.version
                        });
                    }
                }

                if (added.Count > 0 || removed.Count > 0 || versionChanged.Count > 0)
                {
                    results.Add(new
                    {
                        itemType = type,
                        baselineCount = baselineItems.Count,
                        comparisonCount = comparisonItems.Count,
                        addedCount = added.Count,
                        removedCount = removed.Count,
                        versionChangedCount = versionChanged.Count,
                        added = added.Take(30).ToList(),
                        removed = removed.Take(30).ToList(),
                        versionChanged = versionChanged.Take(20).ToList()
                    });
                }
            }

            // Summary
            var totalAdded = results.Sum(r => ((dynamic)r).addedCount);
            var totalRemoved = results.Sum(r => ((dynamic)r).removedCount);
            var totalVersionChanged = results.Sum(r => ((dynamic)r).versionChangedCount);

            return new
            {
                baselineFile = baselinePath,
                comparisonFile = comparisonPath,
                itemTypeFilter = itemType,
                summary = new
                {
                    itemTypesCompared = results.Count,
                    totalAdded,
                    totalRemoved,
                    totalVersionChanged
                },
                itemDiffs = results
            };
        });
    }

    private static Dictionary<string, (string? version, Dictionary<string, string>? metadata)> CollectItems(Build build, string itemType)
    {
        var items = new Dictionary<string, (string? version, Dictionary<string, string>? metadata)>(StringComparer.OrdinalIgnoreCase);

        build.VisitAllChildren<AddItem>(addItem =>
        {
            if (!addItem.Name.Equals(itemType, StringComparison.OrdinalIgnoreCase))
                return;

            var project = GetProjectFile(addItem) ?? "Unknown";
            var projectName = Path.GetFileNameWithoutExtension(project);

            foreach (var item in addItem.Children.OfType<Item>())
            {
                var itemValue = item.Text ?? "";
                var key = $"{itemValue}:{projectName}";

                // Extract metadata
                var metadata = new Dictionary<string, string>();
                string? version = null;

                foreach (var meta in item.Children.OfType<Metadata>())
                {
                    metadata[meta.Name] = meta.Value;
                    if (meta.Name.Equals("Version", StringComparison.OrdinalIgnoreCase))
                        version = meta.Value;
                }

                items[key] = (version, metadata.Count > 0 ? metadata : null);
            }
        });

        return items;
    }

    [McpServerTool, Description("Compares target execution between two builds - shows which targets ran in one build but not the other")]
    public static string DiffTargetExecution(
        [Description("Path to the baseline (older) binlog file")] string baselinePath,
        [Description("Path to the comparison (newer) binlog file")] string comparisonPath,
        [Description("Output format: json (default), markdown, csv, timeline")] string format = "json",
        [Description("Filter to specific project name (optional)")] string? projectFilter = null,
        [Description("Minimum duration in ms to include (default: 0)")] double minDurationMs = 0)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogComparisonWithFormat(baselinePath, comparisonPath, outputFormat, "Target Execution Differences", (baseline, comparison) =>
        {
            var baselineTargets = CollectTargetExecutions(baseline, projectFilter);
            var comparisonTargets = CollectTargetExecutions(comparison, projectFilter);

            var allKeys = baselineTargets.Keys.Union(comparisonTargets.Keys).ToHashSet();

            var onlyInBaseline = new List<object>();
            var onlyInComparison = new List<object>();
            var inBoth = new List<object>();
            var statusChanged = new List<object>();

            foreach (var key in allKeys)
            {
                var hasBaseline = baselineTargets.TryGetValue(key, out var baseInfo);
                var hasComparison = comparisonTargets.TryGetValue(key, out var compInfo);

                // Apply duration filter
                var baseDuration = hasBaseline ? baseInfo.durationMs : 0;
                var compDuration = hasComparison ? compInfo.durationMs : 0;
                if (baseDuration < minDurationMs && compDuration < minDurationMs)
                    continue;

                var parts = key.Split(':', 2);
                var targetName = parts[0];
                var project = parts.Length > 1 ? parts[1] : null;

                if (hasBaseline && hasComparison)
                {
                    // Check for status changes (succeeded vs failed)
                    if (baseInfo.succeeded != compInfo.succeeded)
                    {
                        statusChanged.Add(new
                        {
                            target = targetName,
                            project,
                            baselineSucceeded = baseInfo.succeeded,
                            comparisonSucceeded = compInfo.succeeded,
                            baselineDurationMs = Math.Round(baseDuration, 1),
                            comparisonDurationMs = Math.Round(compDuration, 1)
                        });
                    }
                    else
                    {
                        var durationChange = compDuration - baseDuration;
                        if (Math.Abs(durationChange) > 100) // Only track significant changes
                        {
                            inBoth.Add(new
                            {
                                target = targetName,
                                project,
                                baselineDurationMs = Math.Round(baseDuration, 1),
                                comparisonDurationMs = Math.Round(compDuration, 1),
                                durationChangeMs = Math.Round(durationChange, 1),
                                changePercent = baseDuration > 0 ? Math.Round(durationChange / baseDuration * 100, 1) : 0
                            });
                        }
                    }
                }
                else if (hasComparison && !hasBaseline)
                {
                    onlyInComparison.Add(new
                    {
                        target = targetName,
                        project,
                        succeeded = compInfo.succeeded,
                        durationMs = Math.Round(compDuration, 1)
                    });
                }
                else if (hasBaseline && !hasComparison)
                {
                    onlyInBaseline.Add(new
                    {
                        target = targetName,
                        project,
                        succeeded = baseInfo.succeeded,
                        durationMs = Math.Round(baseDuration, 1)
                    });
                }
            }

            // Sort by duration
            var sortedOnlyBaseline = onlyInBaseline.Cast<dynamic>()
                .OrderByDescending(t => (double)t.durationMs)
                .Take(30)
                .ToList();

            var sortedOnlyComparison = onlyInComparison.Cast<dynamic>()
                .OrderByDescending(t => (double)t.durationMs)
                .Take(30)
                .ToList();

            var sortedTimingChanges = inBoth.Cast<dynamic>()
                .OrderByDescending(t => Math.Abs((double)t.durationChangeMs))
                .Take(30)
                .ToList();

            return new
            {
                baselineFile = baselinePath,
                comparisonFile = comparisonPath,
                projectFilter,
                minDurationMs,
                summary = new
                {
                    baselineTargetCount = baselineTargets.Count,
                    comparisonTargetCount = comparisonTargets.Count,
                    onlyInBaselineCount = onlyInBaseline.Count,
                    onlyInComparisonCount = onlyInComparison.Count,
                    statusChangedCount = statusChanged.Count,
                    significantTimingChanges = inBoth.Count
                },
                statusChanged = statusChanged.Count > 0 ? statusChanged : null,
                onlyInBaseline = sortedOnlyBaseline.Count > 0 ? sortedOnlyBaseline : null,
                onlyInComparison = sortedOnlyComparison.Count > 0 ? sortedOnlyComparison : null,
                timingChanges = sortedTimingChanges.Count > 0 ? sortedTimingChanges : null
            };
        });
    }

    private static Dictionary<string, (bool succeeded, double durationMs)> CollectTargetExecutions(Build build, string? projectFilter)
    {
        var targets = new Dictionary<string, (bool succeeded, double durationMs)>(StringComparer.OrdinalIgnoreCase);

        build.VisitAllChildren<Target>(t =>
        {
            var projectName = t.Project?.Name ?? "Unknown";

            if (projectName.FailsFilter(projectFilter))
                return;

            var key = $"{t.Name}:{projectName}";
            var duration = GetDurationMs(t.StartTime, t.EndTime);

            // If target ran multiple times, sum durations
            if (targets.TryGetValue(key, out var existing))
            {
                targets[key] = (t.Succeeded && existing.succeeded, existing.durationMs + duration);
            }
            else
            {
                targets[key] = (t.Succeeded, duration);
            }
        });

        return targets;
    }

    [McpServerTool, Description("Compares MSBuild import chains between two builds - shows which .props/.targets files were added or removed")]
    public static string DiffImports(
        [Description("Path to the baseline (older) binlog file")] string baselinePath,
        [Description("Path to the comparison (newer) binlog file")] string comparisonPath,
        [Description("Filter to specific project name (optional)")] string? projectFilter = null)
    {
        return ExecuteBinlogComparison(baselinePath, comparisonPath, (baseline, comparison) =>
        {
            var baselineImports = CollectImports(baseline, projectFilter);
            var comparisonImports = CollectImports(comparison, projectFilter);

            var allKeys = baselineImports.Keys.Union(comparisonImports.Keys).ToHashSet();

            var added = new List<object>();
            var removed = new List<object>();
            var inBoth = 0;

            foreach (var key in allKeys)
            {
                var hasBaseline = baselineImports.TryGetValue(key, out var baseInfo);
                var hasComparison = comparisonImports.TryGetValue(key, out var compInfo);

                var parts = key.Split(':', 2);
                var importFile = parts[0];
                var project = parts.Length > 1 ? parts[1] : null;

                if (hasBaseline && hasComparison)
                {
                    inBoth++;
                }
                else if (hasComparison && !hasBaseline)
                {
                    added.Add(new
                    {
                        file = Path.GetFileName(importFile),
                        fullPath = TruncateValue(importFile, 150),
                        project,
                        type = compInfo
                    });
                }
                else if (hasBaseline && !hasComparison)
                {
                    removed.Add(new
                    {
                        file = Path.GetFileName(importFile),
                        fullPath = TruncateValue(importFile, 150),
                        project,
                        type = baseInfo
                    });
                }
            }

            // Categorize by type
            var addedByType = added.Cast<dynamic>()
                .GroupBy(a => (string)a.type)
                .Select(g => new { type = g.Key, count = g.Count(), files = g.Select(f => (string)f.file).Take(10).ToList() })
                .ToList();

            var removedByType = removed.Cast<dynamic>()
                .GroupBy(r => (string)r.type)
                .Select(g => new { type = g.Key, count = g.Count(), files = g.Select(f => (string)f.file).Take(10).ToList() })
                .ToList();

            // Check for significant changes (SDK, Directory.Build, etc.)
            var significantAdded = added.Cast<dynamic>()
                .Where(a => ((string)a.type).Contains("SDK") ||
                           ((string)a.type).Contains("Directory") ||
                           ((string)a.file).Contains("Directory.Build"))
                .ToList();

            var significantRemoved = removed.Cast<dynamic>()
                .Where(r => ((string)r.type).Contains("SDK") ||
                           ((string)r.type).Contains("Directory") ||
                           ((string)r.file).Contains("Directory.Build"))
                .ToList();

            return new
            {
                baselineFile = baselinePath,
                comparisonFile = comparisonPath,
                projectFilter,
                summary = new
                {
                    baselineImportCount = baselineImports.Count,
                    comparisonImportCount = comparisonImports.Count,
                    addedCount = added.Count,
                    removedCount = removed.Count,
                    unchangedCount = inBoth,
                    significantChanges = significantAdded.Count + significantRemoved.Count
                },
                significantChanges = significantAdded.Count > 0 || significantRemoved.Count > 0
                    ? new { added = significantAdded, removed = significantRemoved }
                    : null,
                addedByType = addedByType.Count > 0 ? addedByType : null,
                removedByType = removedByType.Count > 0 ? removedByType : null,
                added = added.Take(30).ToList(),
                removed = removed.Take(30).ToList()
            };
        });
    }

    private static Dictionary<string, string> CollectImports(Build build, string? projectFilter)
    {
        var imports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        build.VisitAllChildren<Import>(import =>
        {
            var projectName = GetProjectName(import) ?? GetProjectFile(import) ?? "Unknown";

            if (projectName.FailsFilter(projectFilter))
                return;

            var importedFile = import.ProjectFilePath ?? import.ImportedProjectFilePath ?? "";
            if (string.IsNullOrEmpty(importedFile))
                return;

            var key = $"{importedFile}:{projectName}";
            imports[key] = importedFile.DetermineImportType();
        });

        return imports;
    }

    [McpServerTool, Description("Analyzes incremental build behavior - shows which targets ran vs were skipped and why")]
    public static string GetIncrementalBuildAnalysis(
        [Description("Path to the binlog file")] string binlogPath)
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

            // Categorize targets
            var executedTargets = targets.Where(t => GetDurationMs(t.StartTime, t.EndTime) > 0).ToList();
            var skippedTargets = targets.Where(t => GetDurationMs(t.StartTime, t.EndTime) == 0).ToList();

            // Find up-to-date messages
            var upToDateMessages = messages
                .Where(m => m.Text != null && (
                    m.Text.Contains("Skipping target", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("up-to-date", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("Building target", StringComparison.OrdinalIgnoreCase) && m.Text.Contains("completely", StringComparison.OrdinalIgnoreCase)))
                .Select(m => new
                {
                    message = m.Text,
                    project = GetProjectName(m)
                })
                .Take(50)
                .ToList();

            // Find rebuild trigger messages
            var rebuildTriggers = messages
                .Where(m => m.Text != null && (
                    m.Text.Contains("out of date", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("newer than", StringComparison.OrdinalIgnoreCase) ||
                    m.Text.Contains("input file", StringComparison.OrdinalIgnoreCase) && m.Text.Contains("changed", StringComparison.OrdinalIgnoreCase)))
                .Select(m => new
                {
                    message = m.Text,
                    project = GetProjectName(m)
                })
                .Take(30)
                .ToList();

            // Group executed targets by project
            var targetsByProject = executedTargets
                .GroupBy(t => t.Project?.Name ?? "Unknown")
                .Select(g => new
                {
                    project = g.Key,
                    executedCount = g.Count(),
                    totalDurationMs = g.Sum(t => GetDurationMs(t.StartTime, t.EndTime)),
                    targets = g.OrderByDescending(t => GetDurationMs(t.StartTime, t.EndTime))
                        .Take(10)
                        .Select(t => new
                        {
                            name = t.Name,
                            durationMs = GetDurationMs(t.StartTime, t.EndTime)
                        })
                        .ToList()
                })
                .OrderByDescending(p => p.totalDurationMs)
                .ToList();

            // Key metrics
            var totalTargets = targets.Count;
            var executedCount = executedTargets.Count;
            var skippedCount = skippedTargets.Count;
            var incrementalEfficiency = totalTargets > 0
                ? Math.Round((double)skippedCount / totalTargets * 100, 1)
                : 0;

            return new
            {
                file = binlogPath,
                summary = new
                {
                    totalTargets,
                    executedTargets = executedCount,
                    skippedTargets = skippedCount,
                    incrementalEfficiencyPercent = incrementalEfficiency,
                    totalExecutionTimeMs = executedTargets.Sum(t => GetDurationMs(t.StartTime, t.EndTime))
                },
                targetsByProject,
                upToDateMessages = upToDateMessages.Count > 0 ? upToDateMessages : null,
                rebuildTriggers = rebuildTriggers.Count > 0 ? rebuildTriggers : null
            };
        });
    }
}
