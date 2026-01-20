using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class GetTargetsTests
{
    [Fact]
    public void GetTargets_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetTargets("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetTargets_RealBinlog_ReturnsTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetTargets(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("totalTargets", out var total));
        Assert.True(total.GetInt32() > 0);

        Assert.True(json.RootElement.TryGetProperty("targets", out var targets));
        Assert.True(targets.GetArrayLength() > 0);

        var firstTarget = targets[0];
        Assert.True(firstTarget.TryGetProperty("name", out _));
        Assert.True(firstTarget.TryGetProperty("durationMs", out _));
        Assert.True(firstTarget.TryGetProperty("startTime", out _));
        Assert.True(firstTarget.TryGetProperty("endTime", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetTargets_WithLimit_RespectsLimit()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetTargets(binlogPath, limit: 5);
        var json = JsonDocument.Parse(result);

        var targets = json.RootElement.GetProperty("targets");
        Assert.True(targets.GetArrayLength() <= 5);
    }

    [Fact]
    public void GetTargets_WithIncludeSkipped_ReturnsSkippedTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetTargets(binlogPath, includeSkipped: true, limit: 100);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("includeSkipped", out var includeSkipped));
        Assert.True(includeSkipped.GetBoolean());

        // skippedTargetCount should be present when includeSkipped is true
        Assert.True(json.RootElement.TryGetProperty("skippedTargetCount", out _));
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlog)) return binlog;
            dir = dir.Parent;
        }
        return null;
    }
}

public class GetTasksTests
{
    [Fact]
    public void GetTasks_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetTasks("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetTasks_RealBinlog_ReturnsTasks()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetTasks(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("totalTasks", out var total));
        Assert.True(total.GetInt32() > 0);

        Assert.True(json.RootElement.TryGetProperty("slowestTaskTypes", out var summary));
        Assert.True(summary.GetArrayLength() > 0);

        Assert.True(json.RootElement.TryGetProperty("tasks", out var tasks));
        Assert.True(tasks.GetArrayLength() > 0);

        Console.WriteLine(result);
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlog)) return binlog;
            dir = dir.Parent;
        }
        return null;
    }
}

public class GetCriticalPathTests
{
    [Fact]
    public void GetCriticalPath_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetCriticalPath("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetCriticalPath_RealBinlog_ReturnsCriticalPath()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetCriticalPath(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("buildDurationMs", out _));
        Assert.True(json.RootElement.TryGetProperty("buildDurationFormatted", out _));
        Assert.True(json.RootElement.TryGetProperty("criticalPathTargets", out var targets));
        Assert.True(targets.GetArrayLength() > 0);

        var firstTarget = targets[0];
        Assert.True(firstTarget.TryGetProperty("percentOfBuild", out _));

        Console.WriteLine(result);
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlog)) return binlog;
            dir = dir.Parent;
        }
        return null;
    }
}
