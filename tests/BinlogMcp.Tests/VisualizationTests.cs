using BinlogMcp.Visualization;
using Xunit;

namespace BinlogMcp.Tests;

public class VisualizationTests
{
    [Fact]
    public void ParsePerformanceJson_SimpleTargets_ParsesCorrectly()
    {
        var json = """
            {
                "targets": [
                    { "name": "Build", "durationMs": 100 },
                    { "name": "Compile", "durationMs": 200 },
                    { "name": "Link", "durationMs": 50 }
                ]
            }
            """;

        var result = ChartRenderer.ParsePerformanceJson(json, "Test", "targets");

        Assert.Equal("Test", result.Title);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal("Compile", result.Items[0].Label); // Sorted by duration descending
        Assert.Equal(200, result.Items[0].Value);
    }

    [Fact]
    public void ParsePerformanceJson_DuplicateTargetNames_AggregatesDurations()
    {
        // This simulates the real-world case where the same target runs in multiple projects
        var json = """
            {
                "targets": [
                    { "name": "DispatchToInnerBuilds", "project": "ProjectA", "durationMs": 100 },
                    { "name": "DispatchToInnerBuilds", "project": "ProjectB", "durationMs": 150 },
                    { "name": "DispatchToInnerBuilds", "project": "ProjectC", "durationMs": 50 },
                    { "name": "Build", "project": "ProjectA", "durationMs": 200 }
                ]
            }
            """;

        var result = ChartRenderer.ParsePerformanceJson(json, "Test", "targets");

        Assert.Equal(2, result.Items.Count); // DispatchToInnerBuilds aggregated into 1

        var dispatchItem = result.Items.First(i => i.Label == "DispatchToInnerBuilds");
        Assert.Equal(300, dispatchItem.Value); // 100 + 150 + 50

        var buildItem = result.Items.First(i => i.Label == "Build");
        Assert.Equal(200, buildItem.Value);
    }

    [Fact]
    public void ParseComparisonJson_DuplicateTargetNames_DoesNotThrow()
    {
        // This was the bug: duplicate keys in ToDictionary would throw
        var baselineJson = """
            {
                "targets": [
                    { "name": "DispatchToInnerBuilds", "project": "ProjectA", "durationMs": 100 },
                    { "name": "DispatchToInnerBuilds", "project": "ProjectB", "durationMs": 150 },
                    { "name": "Build", "project": "ProjectA", "durationMs": 200 }
                ]
            }
            """;

        var currentJson = """
            {
                "targets": [
                    { "name": "DispatchToInnerBuilds", "project": "ProjectA", "durationMs": 80 },
                    { "name": "DispatchToInnerBuilds", "project": "ProjectB", "durationMs": 120 },
                    { "name": "Build", "project": "ProjectA", "durationMs": 180 }
                ]
            }
            """;

        // Should not throw ArgumentException about duplicate keys
        var result = ChartRenderer.ParseComparisonJson(
            baselineJson,
            currentJson,
            "Baseline",
            "Current",
            "Comparison",
            "targets");

        Assert.NotNull(result);
        Assert.NotNull(result.Comparison);
        Assert.Equal(2, result.Comparison.Items.Count);

        var dispatchComparison = result.Comparison.Items.First(i => i.Label == "DispatchToInnerBuilds");
        Assert.Equal(250, dispatchComparison.BaselineValue); // 100 + 150
        Assert.Equal(200, dispatchComparison.CurrentValue);  // 80 + 120
        Assert.Equal(-50, dispatchComparison.Delta);         // Faster
    }

    [Fact]
    public void ParseComparisonJson_NewTargetsInCurrent_IncludedWithZeroBaseline()
    {
        var baselineJson = """
            {
                "targets": [
                    { "name": "Build", "durationMs": 100 }
                ]
            }
            """;

        var currentJson = """
            {
                "targets": [
                    { "name": "Build", "durationMs": 100 },
                    { "name": "NewTarget", "durationMs": 50 }
                ]
            }
            """;

        var result = ChartRenderer.ParseComparisonJson(baselineJson, currentJson, "Baseline", "Current", "Test", "targets");

        Assert.Equal(2, result.Comparison!.Items.Count);

        var newItem = result.Comparison.Items.First(i => i.Label == "NewTarget");
        Assert.Equal(0, newItem.BaselineValue);
        Assert.Equal(50, newItem.CurrentValue);
    }

    [Fact]
    public void ParseComparisonJson_RemovedTargets_IncludedWithZeroCurrent()
    {
        var baselineJson = """
            {
                "targets": [
                    { "name": "Build", "durationMs": 100 },
                    { "name": "OldTarget", "durationMs": 50 }
                ]
            }
            """;

        var currentJson = """
            {
                "targets": [
                    { "name": "Build", "durationMs": 100 }
                ]
            }
            """;

        var result = ChartRenderer.ParseComparisonJson(baselineJson, currentJson, "Baseline", "Current", "Test", "targets");

        Assert.Equal(2, result.Comparison!.Items.Count);

        var oldItem = result.Comparison.Items.First(i => i.Label == "OldTarget");
        Assert.Equal(50, oldItem.BaselineValue);
        Assert.Equal(0, oldItem.CurrentValue);
    }

    [Fact]
    public void ParsePerformanceJson_EmptyTargets_ReturnsEmptyList()
    {
        var json = """{ "targets": [] }""";

        var result = ChartRenderer.ParsePerformanceJson(json, "Test", "targets");

        Assert.Empty(result.Items);
    }

    [Fact]
    public void ParsePerformanceJson_MissingProperty_ReturnsEmptyList()
    {
        var json = """{ "somethingElse": [] }""";

        var result = ChartRenderer.ParsePerformanceJson(json, "Test", "targets");

        Assert.Empty(result.Items);
    }

    [Fact]
    public void ParsePerformanceJson_PreservesCategory()
    {
        var json = """
            {
                "targets": [
                    { "name": "Build", "durationMs": 100, "category": "compilation" },
                    { "name": "Build", "durationMs": 50, "category": "compilation" }
                ]
            }
            """;

        var result = ChartRenderer.ParsePerformanceJson(json, "Test", "targets");

        Assert.Single(result.Items);
        Assert.Equal("compilation", result.Items[0].Category);
        Assert.Equal(150, result.Items[0].Value); // Aggregated
    }

    [Fact]
    public void ParseComparisonJson_SetsCorrectDeltaCategories()
    {
        var baselineJson = """
            {
                "targets": [
                    { "name": "Slower", "durationMs": 100 },
                    { "name": "Faster", "durationMs": 200 },
                    { "name": "Same", "durationMs": 50 }
                ]
            }
            """;

        var currentJson = """
            {
                "targets": [
                    { "name": "Slower", "durationMs": 150 },
                    { "name": "Faster", "durationMs": 100 },
                    { "name": "Same", "durationMs": 50 }
                ]
            }
            """;

        var result = ChartRenderer.ParseComparisonJson(baselineJson, currentJson, "Baseline", "Current", "Test", "targets");

        var slowerItem = result.Items.First(i => i.Label == "Slower");
        Assert.Equal("slower", slowerItem.Category);

        var fasterItem = result.Items.First(i => i.Label == "Faster");
        Assert.Equal("faster", fasterItem.Category);

        var sameItem = result.Items.First(i => i.Label == "Same");
        Assert.Equal("same", sameItem.Category);
    }

    [Fact]
    public void ParseTimelineJson_ParsesEventsCorrectly()
    {
        var json = """
            {
                "buildStartTime": "2024-01-01T10:00:00Z",
                "buildDurationMs": 5000,
                "events": [
                    {
                        "Name": "Build",
                        "Type": "target",
                        "StartMs": 0,
                        "DurationMs": 1000,
                        "Metadata": { "succeeded": true }
                    },
                    {
                        "Name": "Compile",
                        "Type": "target",
                        "StartMs": 1000,
                        "DurationMs": 2000,
                        "Metadata": { "succeeded": true }
                    }
                ]
            }
            """;

        var result = ChartRenderer.ParseTimelineJson(json, "Build Timeline");

        Assert.Equal("Build Timeline", result.Title);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Build", result.Rows[0].Label);
        Assert.Equal("success", result.Rows[0].Status);
    }

    [Fact]
    public void ParseTimelineJson_NestedChildren_ParsesRecursively()
    {
        var json = """
            {
                "buildStartTime": "2024-01-01T10:00:00Z",
                "buildDurationMs": 5000,
                "events": [
                    {
                        "Name": "Project",
                        "Type": "project",
                        "StartMs": 0,
                        "DurationMs": 3000,
                        "Metadata": { "succeeded": true },
                        "Children": [
                            {
                                "Name": "Build",
                                "Type": "target",
                                "StartMs": 100,
                                "DurationMs": 500,
                                "Metadata": { "succeeded": true }
                            }
                        ]
                    }
                ]
            }
            """;

        var result = ChartRenderer.ParseTimelineJson(json, "Timeline");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Project", result.Rows[0].Label);
        Assert.Null(result.Rows[0].Parent);
        Assert.Equal("Build", result.Rows[1].Label);
        Assert.Equal("Project", result.Rows[1].Parent);
    }

    [Fact]
    public void ParseTimelineJson_FailedEvent_SetsFailedStatus()
    {
        var json = """
            {
                "buildStartTime": "2024-01-01T10:00:00Z",
                "buildDurationMs": 1000,
                "events": [
                    {
                        "Name": "FailedTarget",
                        "Type": "target",
                        "StartMs": 0,
                        "DurationMs": 500,
                        "Metadata": { "succeeded": false }
                    }
                ]
            }
            """;

        var result = ChartRenderer.ParseTimelineJson(json, "Timeline");

        Assert.Single(result.Rows);
        Assert.Equal("failed", result.Rows[0].Status);
    }
}
