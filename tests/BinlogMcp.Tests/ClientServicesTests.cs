using System.Text.Json.Nodes;
using BinlogMcp.Client;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

/// <summary>
/// Tests for the client services (VisualizationService, BuildComparisonService).
/// These tests use a mock MCP tool caller that invokes the real BinlogTools.
/// </summary>
public class ClientServicesTests
{
    /// <summary>
    /// Mock tool caller that invokes BinlogTools directly (no MCP server needed).
    /// </summary>
    private class DirectToolCaller : IMcpToolCaller
    {
        public Task<string> CallToolAsync(string toolName, JsonObject arguments)
        {
            var result = toolName switch
            {
                "GetTimeline" => BinlogTools.GetTimeline(
                    arguments["binlogPath"]?.GetValue<string>() ?? "",
                    level: arguments["level"]?.GetValue<string>() ?? "targets"),

                "GetTargets" => BinlogTools.GetTargets(
                    arguments["binlogPath"]?.GetValue<string>() ?? "",
                    limit: arguments["top"]?.GetValue<int>() ?? 50),

                "CompareBinlogs" => BinlogTools.CompareBinlogs(
                    arguments["baselinePath"]?.GetValue<string>() ?? "",
                    arguments["comparisonPath"]?.GetValue<string>() ?? ""),

                "GetIncrementalBuildAnalysis" => BinlogTools.GetIncrementalBuildAnalysis(
                    arguments["binlogPath"]?.GetValue<string>() ?? ""),

                _ => throw new NotSupportedException($"Tool {toolName} not supported in tests")
            };

            return Task.FromResult(result);
        }
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlog))
                return binlog;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindOrchardCoreBinlog(string filename)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test-data", "OrchardCore", filename);
            if (File.Exists(binlog))
                return binlog;
            dir = dir.Parent;
        }
        return null;
    }

    #region VisualizationService Tests

    [Fact]
    public async Task VisualizationService_GetTimelineDataAsync_ReturnsValidData()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var service = new VisualizationService(new DirectToolCaller());
        var result = await service.GetTimelineDataAsync(binlogPath);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Title);
        Assert.True(result.Rows.Count > 0, "Timeline should have rows");
    }

    [Fact]
    public async Task VisualizationService_GetPerformanceDataAsync_ReturnsValidData()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var service = new VisualizationService(new DirectToolCaller());
        var result = await service.GetPerformanceDataAsync(binlogPath);

        Assert.NotNull(result);
        Assert.NotEmpty(result.Title);
        Assert.True(result.Items.Count > 0, "Should have performance items");
    }

    [Fact]
    public async Task VisualizationService_GetComparisonDataAsync_WithDuplicateTargets_DoesNotThrow()
    {
        // This test verifies the bug fix for duplicate key exception
        var oc1 = FindOrchardCoreBinlog("oc-1.binlog");
        var oc2 = FindOrchardCoreBinlog("oc-2.binlog");
        if (oc1 == null || oc2 == null) return;

        var service = new VisualizationService(new DirectToolCaller());

        // This should not throw ArgumentException about duplicate keys
        var result = await service.GetComparisonDataAsync(oc1, oc2);

        Assert.NotNull(result);
        Assert.NotNull(result.Comparison);
        Assert.True(result.Comparison.Items.Count > 0, "Should have comparison items");
    }

    [Fact]
    public async Task VisualizationService_GetComparisonDataAsync_SameFile_ReturnsValidData()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var service = new VisualizationService(new DirectToolCaller());
        var result = await service.GetComparisonDataAsync(binlogPath, binlogPath);

        Assert.NotNull(result);
        Assert.NotNull(result.Comparison);

        // When comparing to itself, all deltas should be zero
        foreach (var item in result.Comparison.Items)
        {
            Assert.Equal(item.BaselineValue, item.CurrentValue);
            Assert.Equal(0, item.Delta);
        }
    }

    #endregion

    #region BuildComparisonService Tests

    [Fact]
    public async Task BuildComparisonService_CompareBuildsAsync_ReturnsValidResult()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var service = new BuildComparisonService(new DirectToolCaller());
        var result = await service.CompareBuildsAsync(binlogPath, binlogPath);

        Assert.NotNull(result);
        Assert.True(result.BaselineSucceeded);
        Assert.True(result.ComparisonSucceeded);
        Assert.Equal(result.BaselineDurationMs, result.ComparisonDurationMs);
        Assert.NotEmpty(result.Conclusion);
    }

    [Fact]
    public async Task BuildComparisonService_CompareBuildsAsync_CleanVsIncremental_DetectsPattern()
    {
        var oc1 = FindOrchardCoreBinlog("oc-1.binlog");
        var oc2 = FindOrchardCoreBinlog("oc-2.binlog");
        if (oc1 == null || oc2 == null) return;

        var service = new BuildComparisonService(new DirectToolCaller());
        var result = await service.CompareBuildsAsync(oc1, oc2);

        Assert.NotNull(result);
        Assert.True(result.ComparisonSucceeded);

        // oc-2 should be significantly faster (incremental build)
        Assert.True(result.DurationChangePercent < -30,
            $"Expected >30% speedup for incremental build, got {result.DurationChangePercent:F1}%");

        // Conclusion should mention incremental pattern
        Assert.True(
            result.Conclusion.Contains("INCREMENTAL", StringComparison.OrdinalIgnoreCase) ||
            result.Conclusion.Contains("faster", StringComparison.OrdinalIgnoreCase),
            $"Conclusion should mention incremental pattern: {result.Conclusion}");

        Assert.True(result.IsLikelyIncrementalComparison,
            "Should detect this as an incremental comparison");
    }

    [Fact]
    public async Task BuildComparisonService_GetIncrementalAnalysisAsync_ReturnsValidResult()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var service = new BuildComparisonService(new DirectToolCaller());
        var result = await service.GetIncrementalAnalysisAsync(binlogPath);

        Assert.NotNull(result);
        Assert.True(result.TotalTargets > 0);
        Assert.True(result.ExecutedTargets >= 0);
        Assert.True(result.SkippedTargets >= 0);
        Assert.Equal(result.TotalTargets, result.ExecutedTargets + result.SkippedTargets);
    }

    #endregion
}
