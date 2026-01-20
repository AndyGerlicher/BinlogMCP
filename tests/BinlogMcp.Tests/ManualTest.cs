using BinlogMcp.Tools;
using BinlogMcp.Visualization;
using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace BinlogMcp.Tests;

public class ManualTest(ITestOutputHelper output)
{
    // Run this to see output from the real binlog file
    // dotnet test --filter "ManualTest" -- xunit.methodDisplay=method

    [Fact]
    public void ListBinlogs_WithRealBinlog_ShowsOutput()
    {
        // Find the repo root (where test.binlog should be)
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "test.binlog")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            // Skip if no binlog found (CI environment)
            return;
        }

        var result = BinlogTools.ListBinlogs(dir.FullName);

        // Just verify it parses and has expected structure
        var json = System.Text.Json.JsonDocument.Parse(result);
        Assert.True(json.RootElement.GetProperty("count").GetInt32() >= 1);

        // Output for manual inspection
        output.WriteLine(result);
    }

    [Fact]
    public void Benchmark_RepeatedBinlogReads()
    {
        // Test with the large sample binlog
        var binlogPath = @"D:\home-src\binlog-samples\msbuild-2.binlog";
        if (!File.Exists(binlogPath))
        {
            output.WriteLine("Skipping benchmark - sample binlog not found");
            return;
        }

        var fileInfo = new FileInfo(binlogPath);
        output.WriteLine($"Benchmarking: {binlogPath}");
        output.WriteLine($"File size: {fileInfo.Length / 1024.0 / 1024.0:F2} MB");
        output.WriteLine($"Cache enabled: {BinlogCache.IsEnabled}");

        // Clear cache to ensure clean test
        BinlogCache.Clear();
        output.WriteLine($"Cache cleared. Current count: {BinlogCache.Count}");

        var sw = new Stopwatch();
        var times = new List<long>();

        // First call - cache miss (cold)
        sw.Restart();
        _ = BinlogTools.GetBuildSummary(binlogPath);
        sw.Stop();
        var coldTime = sw.ElapsedMilliseconds;
        output.WriteLine($"\nFirst call (cache miss): {coldTime}ms");
        output.WriteLine($"Cache count after first call: {BinlogCache.Count}");

        // Subsequent calls - cache hits (warm)
        output.WriteLine("\nSubsequent calls (cache hits):");
        for (int i = 0; i < 5; i++)
        {
            sw.Restart();
            _ = BinlogTools.GetBuildSummary(binlogPath);
            sw.Stop();
            times.Add(sw.ElapsedMilliseconds);
            output.WriteLine($"  Call {i + 1}: {sw.ElapsedMilliseconds}ms");
        }

        output.WriteLine($"\nCached average: {times.Average():F0}ms");
        output.WriteLine($"Speedup: {coldTime / times.Average():F1}x faster");

        // Different tools on the same binlog - all cached
        output.WriteLine("\nDifferent tools on same binlog (all cached):");
        var toolTimes = new List<long>();

        sw.Restart();
        _ = BinlogTools.GetBuildSummary(binlogPath);
        sw.Stop();
        toolTimes.Add(sw.ElapsedMilliseconds);
        output.WriteLine($"  GetBuildSummary: {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        _ = BinlogTools.GetErrors(binlogPath);
        sw.Stop();
        toolTimes.Add(sw.ElapsedMilliseconds);
        output.WriteLine($"  GetErrors: {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        _ = BinlogTools.GetWarnings(binlogPath);
        sw.Stop();
        toolTimes.Add(sw.ElapsedMilliseconds);
        output.WriteLine($"  GetWarnings: {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        _ = BinlogTools.GetTargets(binlogPath);
        sw.Stop();
        toolTimes.Add(sw.ElapsedMilliseconds);
        output.WriteLine($"  GetTargets: {sw.ElapsedMilliseconds}ms");

        output.WriteLine($"\nTotal for 4 tools (cached): {toolTimes.Sum()}ms");
        output.WriteLine($"Before caching would have been: ~{coldTime * 4}ms");
        output.WriteLine($"Time saved: ~{(coldTime * 4) - toolTimes.Sum()}ms");

        // Show cache stats
        var stats = BinlogCache.GetStats();
        output.WriteLine($"\nCache stats:");
        output.WriteLine($"  Cached count: {stats.CachedCount}");
        output.WriteLine($"  Estimated memory: {stats.EstimatedMemoryMB:F1} MB");
    }

    [Fact]
    public void TestVisualizationParsing()
    {
        var binlogPath = @"D:\home-src\binlog-samples\msbuild-1.binlog";
        if (!File.Exists(binlogPath))
        {
            output.WriteLine("Skipping - sample binlog not found");
            return;
        }

        // Test GetTimeline parsing
        output.WriteLine("Testing GetTimeline parsing:");
        var timelineJson = BinlogTools.GetTimeline(binlogPath, level: "targets");
        output.WriteLine($"Timeline JSON (first 500 chars): {timelineJson[..Math.Min(500, timelineJson.Length)]}...");

        var timelineData = ChartRenderer.ParseTimelineJson(timelineJson, "Test Timeline");
        output.WriteLine($"Parsed timeline: {timelineData.Rows.Count} rows");
        output.WriteLine($"Build duration: {(timelineData.BuildEnd - timelineData.BuildStart).TotalSeconds:F1}s");

        if (timelineData.Rows.Count > 0)
        {
            output.WriteLine($"First row: {timelineData.Rows[0].Label} ({timelineData.Rows[0].Category})");
        }

        // Test GetTargets parsing
        output.WriteLine("\nTesting GetTargets parsing:");
        var targetsJson = BinlogTools.GetTargets(binlogPath, limit: 10);
        output.WriteLine($"Targets JSON (first 500 chars): {targetsJson[..Math.Min(500, targetsJson.Length)]}...");

        var barData = ChartRenderer.ParsePerformanceJson(targetsJson, "Test Targets", "targets");
        output.WriteLine($"Parsed targets: {barData.Items.Count} items");

        if (barData.Items.Count > 0)
        {
            output.WriteLine($"Slowest: {barData.Items[0].Label} ({barData.Items[0].Value:F0}ms)");
        }

        Assert.True(timelineData.Rows.Count > 0, "Timeline should have rows");
        Assert.True(barData.Items.Count > 0, "Targets should have items");
    }

    [Fact]
    public void TestOrchardCoreComparison()
    {
        var oc1 = @"D:\home-src\binlog-mcp\test-data\OrchardCore\oc-1.binlog";
        var oc2 = @"D:\home-src\binlog-mcp\test-data\OrchardCore\oc-2.binlog";

        if (!File.Exists(oc1) || !File.Exists(oc2))
        {
            output.WriteLine("Skipping - OrchardCore binlogs not found");
            return;
        }

        output.WriteLine("Comparing OrchardCore builds:");
        output.WriteLine($"  oc-1 (clean): {oc1}");
        output.WriteLine($"  oc-2 (incremental): {oc2}");

        // Get incremental analysis for both
        output.WriteLine("\n=== oc-1 Incremental Analysis ===");
        var oc1Analysis = BinlogTools.GetIncrementalBuildAnalysis(oc1);
        var json1 = System.Text.Json.JsonDocument.Parse(oc1Analysis);
        var summary1 = json1.RootElement.GetProperty("summary");
        output.WriteLine($"Total targets: {summary1.GetProperty("totalTargets").GetInt32()}");
        output.WriteLine($"Executed targets: {summary1.GetProperty("executedTargets").GetInt32()}");
        output.WriteLine($"Skipped targets: {summary1.GetProperty("skippedTargets").GetInt32()}");
        output.WriteLine($"Incremental efficiency: {summary1.GetProperty("incrementalEfficiencyPercent").GetDouble():F1}%");

        output.WriteLine("\n=== oc-2 Incremental Analysis ===");
        var oc2Analysis = BinlogTools.GetIncrementalBuildAnalysis(oc2);
        var json2 = System.Text.Json.JsonDocument.Parse(oc2Analysis);
        var summary2 = json2.RootElement.GetProperty("summary");
        output.WriteLine($"Total targets: {summary2.GetProperty("totalTargets").GetInt32()}");
        output.WriteLine($"Executed targets: {summary2.GetProperty("executedTargets").GetInt32()}");
        output.WriteLine($"Skipped targets: {summary2.GetProperty("skippedTargets").GetInt32()}");
        output.WriteLine($"Incremental efficiency: {summary2.GetProperty("incrementalEfficiencyPercent").GetDouble():F1}%");

        // Run comparison
        output.WriteLine("\n=== CompareBinlogs Result ===");
        var comparison = BinlogTools.CompareBinlogs(oc1, oc2);
        var compJson = System.Text.Json.JsonDocument.Parse(comparison);

        var buildTypeAnalysis = compJson.RootElement.GetProperty("buildTypeAnalysis");
        var baseline = buildTypeAnalysis.GetProperty("baseline");
        var comp = buildTypeAnalysis.GetProperty("comparison");

        output.WriteLine($"\nBaseline (oc-1):");
        output.WriteLine($"  Total targets: {baseline.GetProperty("totalTargets").GetInt32()}");
        output.WriteLine($"  Executed: {baseline.GetProperty("executedTargets").GetInt32()}");
        output.WriteLine($"  Skipped: {baseline.GetProperty("skippedTargets").GetInt32()}");
        output.WriteLine($"  Skipped %: {baseline.GetProperty("skippedPercent").GetDouble():F1}%");
        output.WriteLine($"  Up-to-date messages: {baseline.GetProperty("upToDateMessages").GetInt32()}");
        output.WriteLine($"  Compilation targets: {baseline.GetProperty("compilationTargetsExecuted").GetInt32()}");
        output.WriteLine($"  isLikelyCleanBuild: {baseline.GetProperty("isLikelyCleanBuild").GetBoolean()}");
        output.WriteLine($"  isLikelyIncrementalBuild: {baseline.GetProperty("isLikelyIncrementalBuild").GetBoolean()}");

        output.WriteLine($"\nComparison (oc-2):");
        output.WriteLine($"  Total targets: {comp.GetProperty("totalTargets").GetInt32()}");
        output.WriteLine($"  Executed: {comp.GetProperty("executedTargets").GetInt32()}");
        output.WriteLine($"  Skipped: {comp.GetProperty("skippedTargets").GetInt32()}");
        output.WriteLine($"  Skipped %: {comp.GetProperty("skippedPercent").GetDouble():F1}%");
        output.WriteLine($"  Up-to-date messages: {comp.GetProperty("upToDateMessages").GetInt32()}");
        output.WriteLine($"  Compilation targets: {comp.GetProperty("compilationTargetsExecuted").GetInt32()}");
        output.WriteLine($"  isLikelyCleanBuild: {comp.GetProperty("isLikelyCleanBuild").GetBoolean()}");
        output.WriteLine($"  isLikelyIncrementalBuild: {comp.GetProperty("isLikelyIncrementalBuild").GetBoolean()}");

        output.WriteLine($"\n=== CONCLUSION ===");
        output.WriteLine(compJson.RootElement.GetProperty("conclusion").GetString());
    }
}
