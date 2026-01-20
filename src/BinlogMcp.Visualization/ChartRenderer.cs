using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace BinlogMcp.Visualization;

/// <summary>
/// Main entry point for rendering charts to HTML files.
/// </summary>
public static class ChartRenderer
{
    /// <summary>
    /// Renders a timeline chart and saves to file.
    /// </summary>
    public static ChartResult RenderTimeline(TimelineData data, string? outputPath = null)
    {
        var html = TimelineChart.Generate(data);
        var filePath = outputPath ?? GetDefaultPath("timeline");
        File.WriteAllText(filePath, html);
        return new ChartResult { FilePath = filePath, Html = html };
    }

    /// <summary>
    /// Renders a bar chart and saves to file.
    /// </summary>
    public static ChartResult RenderBarChart(BarChartData data, string? outputPath = null)
    {
        var html = BarChart.Generate(data);
        var filePath = outputPath ?? GetDefaultPath("chart");
        File.WriteAllText(filePath, html);
        return new ChartResult { FilePath = filePath, Html = html };
    }

    /// <summary>
    /// Creates timeline data from MCP GetTimeline JSON output.
    /// </summary>
    public static TimelineData ParseTimelineJson(string json, string title = "Build Timeline")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Parse build start time (ISO format)
        var buildStart = DateTime.Now;
        if (root.TryGetProperty("buildStartTime", out var bst) && bst.ValueKind == JsonValueKind.String)
        {
            buildStart = DateTime.Parse(bst.GetString()!);
        }

        // Parse build duration to calculate end time
        var buildDurationMs = 0.0;
        if (root.TryGetProperty("buildDurationMs", out var bdm))
        {
            buildDurationMs = bdm.GetDouble();
        }
        var buildEnd = buildStart.AddMilliseconds(buildDurationMs);

        var rows = new List<TimelineRow>();

        // Parse events array (GetTimeline output structure)
        if (root.TryGetProperty("events", out var events))
        {
            ParseTimelineEvents(events, buildStart, rows, null);
        }

        return new TimelineData
        {
            Title = title,
            BuildStart = buildStart,
            BuildEnd = buildEnd,
            Rows = rows
        };
    }

    /// <summary>
    /// Recursively parse timeline events and their children.
    /// </summary>
    private static void ParseTimelineEvents(JsonElement events, DateTime buildStart, List<TimelineRow> rows, string? parent)
    {
        foreach (var evt in events.EnumerateArray())
        {
            var name = evt.TryGetProperty("Name", out var n) ? n.GetString() ?? "Unknown" : "Unknown";
            var type = evt.TryGetProperty("Type", out var t) ? t.GetString() ?? "target" : "target";
            var startMs = evt.TryGetProperty("StartMs", out var sm) ? sm.GetDouble() : 0;
            var durationMs = evt.TryGetProperty("DurationMs", out var dm) ? dm.GetDouble() : 0;

            // Get succeeded from Metadata
            var succeeded = true;
            if (evt.TryGetProperty("Metadata", out var metadata) &&
                metadata.TryGetProperty("succeeded", out var succ))
            {
                succeeded = succ.ValueKind == JsonValueKind.True;
            }

            var start = buildStart.AddMilliseconds(startMs);
            var end = buildStart.AddMilliseconds(startMs + durationMs);

            rows.Add(new TimelineRow
            {
                Label = name,
                Category = type,
                Start = start,
                End = end,
                Parent = parent,
                Status = succeeded ? "success" : "failed"
            });

            // Recursively add children
            if (evt.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                ParseTimelineEvents(children, buildStart, rows, name);
            }
        }
    }

    /// <summary>
    /// Creates bar chart data from MCP performance JSON output.
    /// Aggregates items with the same name (e.g., targets that run in multiple projects).
    /// </summary>
    public static BarChartData ParsePerformanceJson(string json, string title = "Performance", string itemsProperty = "targets")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Use dictionary to aggregate items with the same name
        var aggregated = new Dictionary<string, (double totalDuration, string? category)>();

        if (root.TryGetProperty(itemsProperty, out var itemsArray))
        {
            foreach (var item in itemsArray.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() :
                           item.TryGetProperty("target", out var t) ? t.GetString() :
                           item.TryGetProperty("project", out var p) ? p.GetString() : "Unknown";

                var duration = item.TryGetProperty("durationMs", out var d) ? d.GetDouble() :
                               item.TryGetProperty("totalDurationMs", out var td) ? td.GetDouble() :
                               item.TryGetProperty("duration", out var dur) ? dur.GetDouble() : 0;

                var category = item.TryGetProperty("category", out var c) ? c.GetString() : null;

                var label = name ?? "Unknown";
                if (aggregated.TryGetValue(label, out var existing))
                {
                    aggregated[label] = (existing.totalDuration + duration, existing.category ?? category);
                }
                else
                {
                    aggregated[label] = (duration, category);
                }
            }
        }

        var items = aggregated
            .Select(kvp => new BarChartItem
            {
                Label = kvp.Key,
                Value = kvp.Value.totalDuration,
                Category = kvp.Value.category
            })
            .OrderByDescending(i => i.Value)
            .ToList();

        return new BarChartData
        {
            Title = title,
            XAxisLabel = "Item",
            YAxisLabel = "Duration",
            Items = items
        };
    }

    /// <summary>
    /// Creates comparison bar chart data from two performance JSON outputs.
    /// </summary>
    public static BarChartData ParseComparisonJson(
        string baselineJson,
        string currentJson,
        string baselineLabel = "Baseline",
        string currentLabel = "Current",
        string title = "Performance Comparison",
        string itemsProperty = "targets")
    {
        var baseline = ParsePerformanceJson(baselineJson, title, itemsProperty);
        var current = ParsePerformanceJson(currentJson, title, itemsProperty);

        var baselineDict = baseline.Items.ToDictionary(i => i.Label, i => i.Value);
        var currentDict = current.Items.ToDictionary(i => i.Label, i => i.Value);

        var allLabels = baselineDict.Keys.Union(currentDict.Keys).ToHashSet();

        var comparisonItems = allLabels.Select(label => new ComparisonItem
        {
            Label = label,
            BaselineValue = baselineDict.GetValueOrDefault(label),
            CurrentValue = currentDict.GetValueOrDefault(label)
        }).ToList();

        // Also include top items for the main Items list
        var items = comparisonItems
            .OrderByDescending(c => Math.Max(c.BaselineValue, c.CurrentValue))
            .Select(c => new BarChartItem
            {
                Label = c.Label,
                Value = c.CurrentValue,
                Category = c.Delta > 0 ? "slower" : c.Delta < 0 ? "faster" : "same"
            })
            .ToList();

        return new BarChartData
        {
            Title = title,
            XAxisLabel = "Item",
            YAxisLabel = "Duration",
            Items = items,
            Comparison = new BarChartComparison
            {
                BaselineLabel = baselineLabel,
                CurrentLabel = currentLabel,
                Items = comparisonItems.OrderByDescending(c => Math.Max(c.BaselineValue, c.CurrentValue)).ToList()
            }
        };
    }

    /// <summary>
    /// Opens an HTML file in the default browser.
    /// </summary>
    public static void OpenInBrowser(string filePath)
    {
        var url = new Uri(filePath).AbsoluteUri;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Process.Start("xdg-open", url);
        }
    }

    /// <summary>
    /// Renders and opens a timeline chart in the browser.
    /// </summary>
    public static ChartResult RenderAndOpenTimeline(TimelineData data, string? outputPath = null)
    {
        var result = RenderTimeline(data, outputPath);
        OpenInBrowser(result.FilePath);
        return result;
    }

    /// <summary>
    /// Renders and opens a bar chart in the browser.
    /// </summary>
    public static ChartResult RenderAndOpenBarChart(BarChartData data, string? outputPath = null)
    {
        var result = RenderBarChart(data, outputPath);
        OpenInBrowser(result.FilePath);
        return result;
    }

    private static string GetDefaultPath(string prefix)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{prefix}-{timestamp}.html";
        return Path.Combine(Path.GetTempPath(), fileName);
    }

    private static DateTime ParseDateTime(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.String)
                return DateTime.Parse(prop.GetString()!);
        }
        return DateTime.MinValue;
    }
}
