using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class PerformanceReportTests
{
    [Fact]
    public void GetPerformanceReport_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetPerformanceReport("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetPerformanceReport_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetPerformanceReport(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("buildDurationMs", out _));
        Assert.True(summary.TryGetProperty("totalTargets", out _));
        Assert.True(summary.TryGetProperty("totalTasks", out _));

        Assert.True(json.RootElement.TryGetProperty("slowestTargets", out _));
        Assert.True(json.RootElement.TryGetProperty("slowestTasks", out _));
        Assert.True(json.RootElement.TryGetProperty("taskTypeSummary", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetPerformanceReport_ReturnsProjectTiming()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetPerformanceReport(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectTiming", out var timing));
        Assert.True(timing.GetArrayLength() > 0);
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

public class CompilerPerformanceTests
{
    [Fact]
    public void GetCompilerPerformance_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetCompilerPerformance("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetCompilerPerformance_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetCompilerPerformance(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalCompileTimeMs", out _));
        Assert.True(summary.TryGetProperty("compilerInvocations", out _));

        Assert.True(json.RootElement.TryGetProperty("compilerSummary", out _));
        Assert.True(json.RootElement.TryGetProperty("compilationByProject", out _));

        Console.WriteLine(result);
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

public class ParallelismAnalysisTests
{
    [Fact]
    public void GetParallelismAnalysis_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetParallelismAnalysis("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetParallelismAnalysis_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetParallelismAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("buildDurationMs", out _));
        Assert.True(summary.TryGetProperty("maxParallelProjects", out _));
        Assert.True(summary.TryGetProperty("avgParallelProjects", out _));

        Assert.True(json.RootElement.TryGetProperty("projectOverlaps", out _));

        Console.WriteLine(result);
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

public class SlowOperationsTests
{
    [Fact]
    public void GetSlowOperations_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetSlowOperations("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetSlowOperations_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetSlowOperations(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("buildDurationMs", out _));
        Assert.True(summary.TryGetProperty("totalCopyTimeMs", out _));

        Assert.True(json.RootElement.TryGetProperty("slowestTasks", out _));
        Assert.True(json.RootElement.TryGetProperty("taskTypeSummary", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetSlowOperations_WithMinDuration_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetSlowOperations(binlogPath, minDurationMs: 500);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("minDurationMs", out var minDuration));
        Assert.Equal(500, minDuration.GetInt32());
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

public class ProjectPerformanceTests
{
    [Fact]
    public void GetProjectPerformance_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetProjectPerformance("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetProjectPerformance_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProjectPerformance(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("buildDurationMs", out _));
        Assert.True(summary.TryGetProperty("projectCount", out _));
        Assert.True(summary.TryGetProperty("totalCompilerTimeMs", out _));

        Assert.True(json.RootElement.TryGetProperty("projects", out var projects));
        Assert.True(projects.GetArrayLength() > 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetProjectPerformance_WithTargetDetails_IncludesSlowTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProjectPerformance(binlogPath, includeTargetDetails: true);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projects", out var projects));
        if (projects.GetArrayLength() > 0)
        {
            var firstProject = projects[0];
            Assert.True(firstProject.TryGetProperty("slowestTargets", out _));
        }
    }

    [Fact]
    public void GetProjectPerformance_MarkdownFormat_ReturnsMarkdown()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProjectPerformance(binlogPath, format: "markdown");
        Assert.Contains("# Project Performance", result);
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

public class ComparePerformanceTests
{
    [Fact]
    public void ComparePerformance_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.ComparePerformance("/nonexistent/file1.binlog", "/nonexistent/file2.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void ComparePerformance_SameBinlog_ReturnsSimilarVerdict()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.ComparePerformance(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("verdict", out var verdict));
        Assert.Equal("SIMILAR", verdict.GetString());

        Assert.True(summary.TryGetProperty("overallChangeMs", out var changeMs));
        Assert.Equal(0, changeMs.GetDouble());
    }

    [Fact]
    public void ComparePerformance_RealBinlog_ReturnsComparison()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.ComparePerformance(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("baselineDurationMs", out _));
        Assert.True(summary.TryGetProperty("comparisonDurationMs", out _));
        Assert.True(summary.TryGetProperty("overallChangePercent", out _));
        Assert.True(summary.TryGetProperty("baselineParallelism", out _));
        Assert.True(summary.TryGetProperty("comparisonParallelism", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void ComparePerformance_MarkdownFormat_ReturnsMarkdown()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.ComparePerformance(binlogPath, binlogPath, format: "markdown");
        Assert.Contains("# Performance Comparison", result);
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

public class ParallelismBlockersTests
{
    [Fact]
    public void GetParallelismBlockers_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetParallelismBlockers("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetParallelismBlockers_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetParallelismBlockers(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("buildDurationMs", out _));
        Assert.True(summary.TryGetProperty("projectCount", out _));
        Assert.True(summary.TryGetProperty("serializationPercent", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetParallelismBlockers_WithMinDuration_FiltersBlockers()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetParallelismBlockers(binlogPath, minBlockerDurationMs: 1000);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("minBlockerDurationMs", out var minDuration));
        Assert.Equal(1000, minDuration.GetDouble());
    }

    [Fact]
    public void GetParallelismBlockers_MarkdownFormat_ReturnsMarkdown()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetParallelismBlockers(binlogPath, format: "markdown");
        Assert.Contains("# Parallelism Blockers", result);
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

public class AnalyzeTargetTests
{
    [Fact]
    public void AnalyzeTarget_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.AnalyzeTarget("/nonexistent/file.binlog", "Build");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void AnalyzeTarget_TargetNotFound_ReturnsError()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.AnalyzeTarget(binlogPath, "NonExistentTarget12345");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void AnalyzeTarget_RealTarget_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        // CoreCompile is a common target in .NET builds
        var result = BinlogTools.AnalyzeTarget(binlogPath, "CoreCompile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("executionCount", out _));
        Assert.True(summary.TryGetProperty("totalDurationMs", out _));

        Assert.True(json.RootElement.TryGetProperty("executions", out var executions));
        if (executions.GetArrayLength() > 0)
        {
            var firstExec = executions[0];
            Assert.True(firstExec.TryGetProperty("project", out _));
            Assert.True(firstExec.TryGetProperty("durationMs", out _));
            Assert.True(firstExec.TryGetProperty("taskCount", out _));
        }

        Console.WriteLine(result);
    }

    [Fact]
    public void AnalyzeTarget_WithProjectFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.AnalyzeTarget(binlogPath, "CoreCompile", projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectFilter", out var filter));
        Assert.Equal("BinlogMcp", filter.GetString());
    }

    [Fact]
    public void AnalyzeTarget_MarkdownFormat_ReturnsMarkdown()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.AnalyzeTarget(binlogPath, "CoreCompile", format: "markdown");
        Assert.Contains("# Target Analysis:", result);
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
