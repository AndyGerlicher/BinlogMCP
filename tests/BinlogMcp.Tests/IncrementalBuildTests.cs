using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class IncrementalBuildTests
{
    [Fact]
    public void GetIncrementalBuildAnalysis_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetIncrementalBuildAnalysis("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetIncrementalBuildAnalysis_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetIncrementalBuildAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));

        Assert.True(summary.TryGetProperty("totalTargets", out var total));
        Assert.True(total.GetInt32() > 0);

        Assert.True(summary.TryGetProperty("executedTargets", out _));
        Assert.True(summary.TryGetProperty("skippedTargets", out _));
        Assert.True(summary.TryGetProperty("incrementalEfficiencyPercent", out _));
        Assert.True(summary.TryGetProperty("totalExecutionTimeMs", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetIncrementalBuildAnalysis_ReturnsTargetsByProject()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetIncrementalBuildAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("targetsByProject", out var byProject));
        Assert.True(byProject.GetArrayLength() > 0);

        var firstProject = byProject[0];
        Assert.True(firstProject.TryGetProperty("project", out _));
        Assert.True(firstProject.TryGetProperty("executedCount", out _));
        Assert.True(firstProject.TryGetProperty("totalDurationMs", out _));
        Assert.True(firstProject.TryGetProperty("targets", out _));
    }

    [Fact]
    public void GetIncrementalBuildAnalysis_EfficiencyIsValid()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetIncrementalBuildAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        var summary = json.RootElement.GetProperty("summary");
        var efficiency = summary.GetProperty("incrementalEfficiencyPercent").GetDouble();

        // Efficiency should be between 0 and 100
        Assert.InRange(efficiency, 0, 100);

        // Verify the math: executed + skipped = total
        var total = summary.GetProperty("totalTargets").GetInt32();
        var executed = summary.GetProperty("executedTargets").GetInt32();
        var skipped = summary.GetProperty("skippedTargets").GetInt32();
        Assert.Equal(total, executed + skipped);
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
}
