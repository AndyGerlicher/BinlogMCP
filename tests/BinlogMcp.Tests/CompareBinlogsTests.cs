using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class CompareBinlogsTests
{
    [Fact]
    public void CompareBinlogs_BaselineNotFound_ReturnsError()
    {
        var result = BinlogTools.CompareBinlogs("/nonexistent/baseline.binlog", "/nonexistent/comparison.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Baseline", error.GetString());
    }

    [Fact]
    public void CompareBinlogs_ComparisonNotFound_ReturnsError()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.CompareBinlogs(binlogPath, "/nonexistent/comparison.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Comparison", error.GetString());
    }

    [Fact]
    public void CompareBinlogs_SameFile_ReturnsNoChanges()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        // Compare file with itself - should show no changes
        var result = BinlogTools.CompareBinlogs(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("baseline", out var baseline));
        Assert.True(summary.TryGetProperty("comparison", out var comparison));

        // Durations should be identical
        Assert.Equal(
            baseline.GetProperty("durationMs").GetDouble(),
            comparison.GetProperty("durationMs").GetDouble());

        // No new or fixed errors when comparing to itself
        Assert.True(json.RootElement.TryGetProperty("errors", out var errors));
        Assert.Equal(0, errors.GetProperty("newCount").GetInt32());
        Assert.Equal(0, errors.GetProperty("fixedCount").GetInt32());

        Console.WriteLine(result);
    }

    [Fact]
    public void CompareBinlogs_DifferentFiles_ReturnsValidComparison()
    {
        var testBinlog = FindTestBinlog();
        var errorBinlog = FindErrorBinlog();
        if (testBinlog == null || errorBinlog == null)
            return;

        // Compare successful build with error build
        var result = BinlogTools.CompareBinlogs(testBinlog, errorBinlog);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("baselineFile", out _));
        Assert.True(json.RootElement.TryGetProperty("comparisonFile", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));

        // Baseline should succeed, comparison should fail
        Assert.True(summary.GetProperty("baseline").GetProperty("succeeded").GetBoolean());
        Assert.False(summary.GetProperty("comparison").GetProperty("succeeded").GetBoolean());

        // Should have new errors in the comparison
        Assert.True(json.RootElement.TryGetProperty("errors", out var errors));
        Assert.True(errors.GetProperty("newCount").GetInt32() > 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void CompareBinlogs_ReturnsTargetTimingChanges()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.CompareBinlogs(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        // targetTimingChanges should exist (may be empty if no significant changes)
        Assert.True(json.RootElement.TryGetProperty("targetTimingChanges", out var changes));
        Assert.Equal(JsonValueKind.Array, changes.ValueKind);
    }

    [Fact]
    public void CompareBinlogs_IncludesBuildTypeAnalysis()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.CompareBinlogs(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        // buildTypeAnalysis should exist with baseline and comparison sections
        Assert.True(json.RootElement.TryGetProperty("buildTypeAnalysis", out var analysis));
        Assert.True(analysis.TryGetProperty("baseline", out var baselineAnalysis));
        Assert.True(analysis.TryGetProperty("comparison", out var comparisonAnalysis));

        // Both should have the required properties
        Assert.True(baselineAnalysis.TryGetProperty("totalTargets", out _));
        Assert.True(baselineAnalysis.TryGetProperty("executedTargets", out _));
        Assert.True(baselineAnalysis.TryGetProperty("skippedTargets", out _));
        Assert.True(baselineAnalysis.TryGetProperty("skippedPercent", out _));
        Assert.True(baselineAnalysis.TryGetProperty("isLikelyCleanBuild", out _));
        Assert.True(baselineAnalysis.TryGetProperty("isLikelyIncrementalBuild", out _));

        Assert.True(comparisonAnalysis.TryGetProperty("totalTargets", out _));
        Assert.True(comparisonAnalysis.TryGetProperty("isLikelyCleanBuild", out _));
    }

    [Fact]
    public void CompareBinlogs_IncludesConclusion()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.CompareBinlogs(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        // conclusion should exist and be a non-empty string
        Assert.True(json.RootElement.TryGetProperty("conclusion", out var conclusion));
        Assert.Equal(JsonValueKind.String, conclusion.ValueKind);
        Assert.False(string.IsNullOrEmpty(conclusion.GetString()));
    }

    [Fact]
    public void CompareBinlogs_CleanVsIncremental_DetectsIncrementalBuild()
    {
        // This test requires the OrchardCore binlogs
        var oc1 = FindOrchardCoreBinlog("oc-1.binlog");
        var oc2 = FindOrchardCoreBinlog("oc-2.binlog");
        if (oc1 == null || oc2 == null)
            return; // Skip if test data not available

        var result = BinlogTools.CompareBinlogs(oc1, oc2);
        var json = JsonDocument.Parse(result);

        // Check build type analysis
        Assert.True(json.RootElement.TryGetProperty("buildTypeAnalysis", out var analysis));
        var baselineAnalysis = analysis.GetProperty("baseline");
        var comparisonAnalysis = analysis.GetProperty("comparison");

        // Both builds have similar target counts (this is a large solution)
        Assert.True(baselineAnalysis.TryGetProperty("totalTargets", out _));
        Assert.True(baselineAnalysis.TryGetProperty("executedTargets", out _));
        Assert.True(baselineAnalysis.TryGetProperty("skippedPercent", out _));

        // Get duration comparison
        var summary = json.RootElement.GetProperty("summary");
        var baselineDurationMs = summary.GetProperty("baseline").GetProperty("durationMs").GetDouble();
        var comparisonDurationMs = summary.GetProperty("comparison").GetProperty("durationMs").GetDouble();

        // oc-2 should be significantly faster (it's an incremental build)
        Assert.True(comparisonDurationMs < baselineDurationMs * 0.7,
            $"Expected incremental build to be at least 30% faster. Baseline: {baselineDurationMs}ms, Comparison: {comparisonDurationMs}ms");

        // Conclusion should mention incremental build
        var conclusion = json.RootElement.GetProperty("conclusion").GetString();
        Assert.NotNull(conclusion);

        // Should detect incremental pattern based on duration difference
        Assert.True(
            conclusion.Contains("INCREMENTAL", StringComparison.OrdinalIgnoreCase) ||
            conclusion.Contains("faster", StringComparison.OrdinalIgnoreCase) ||
            conclusion.Contains("cached", StringComparison.OrdinalIgnoreCase),
            $"Conclusion should mention incremental build detection. Got: {conclusion}");

        Console.WriteLine($"Conclusion: {conclusion}");
        Console.WriteLine($"Baseline duration: {baselineDurationMs}ms");
        Console.WriteLine($"Comparison duration: {comparisonDurationMs}ms");
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

    private static string? FindErrorBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test-data", "error.binlog");
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
}
