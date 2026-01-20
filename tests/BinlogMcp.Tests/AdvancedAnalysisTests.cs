using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class UnusedProjectOutputsTests
{
    [Fact]
    public void GetUnusedProjectOutputs_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetUnusedProjectOutputs("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetUnusedProjectOutputs_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetUnusedProjectOutputs(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalProjects", out _));
        Assert.True(summary.TryGetProperty("unreferencedProjects", out _));
        Assert.True(summary.TryGetProperty("potentiallyUnused", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetUnusedProjectOutputs_ExcludeTestsParameter_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var resultWithTests = BinlogTools.GetUnusedProjectOutputs(binlogPath, excludeTests: false);
        var jsonWithTests = JsonDocument.Parse(resultWithTests);

        var resultWithoutTests = BinlogTools.GetUnusedProjectOutputs(binlogPath, excludeTests: true);
        var jsonWithoutTests = JsonDocument.Parse(resultWithoutTests);

        // Both should have the excludeTests property
        Assert.True(jsonWithTests.RootElement.TryGetProperty("excludeTests", out var withTests));
        Assert.False(withTests.GetBoolean());

        Assert.True(jsonWithoutTests.RootElement.TryGetProperty("excludeTests", out var withoutTests));
        Assert.True(withoutTests.GetBoolean());
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

public class SdkFrameworkMismatchTests
{
    [Fact]
    public void GetSdkFrameworkMismatch_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetSdkFrameworkMismatch("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetSdkFrameworkMismatch_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetSdkFrameworkMismatch(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalProjects", out _));
        Assert.True(summary.TryGetProperty("frameworkMismatch", out _));
        Assert.True(summary.TryGetProperty("packageVersionConflicts", out _));

        Assert.True(json.RootElement.TryGetProperty("targetFrameworks", out _));

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

public class FileAccessPatternsTests
{
    [Fact]
    public void GetFileAccessPatterns_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetFileAccessPatterns("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetFileAccessPatterns_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetFileAccessPatterns(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalFilesTracked", out _));
        Assert.True(summary.TryGetProperty("frequentlyAccessedFiles", out _));
        Assert.True(summary.TryGetProperty("sharedAcrossProjects", out _));

        Assert.True(json.RootElement.TryGetProperty("byAccessType", out _));
        Assert.True(json.RootElement.TryGetProperty("byExtension", out _));
        Assert.True(json.RootElement.TryGetProperty("hotFiles", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetFileAccessPatterns_WithMinAccess_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetFileAccessPatterns(binlogPath, minAccess: 5);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("minAccess", out var minAccess));
        Assert.Equal(5, minAccess.GetInt32());
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

public class WarningTrendsAnalysisTests
{
    [Fact]
    public void GetWarningTrendsAnalysis_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetWarningTrendsAnalysis("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetWarningTrendsAnalysis_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetWarningTrendsAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalWarnings", out _));
        Assert.True(summary.TryGetProperty("uniqueCodes", out _));
        Assert.True(summary.TryGetProperty("projectsWithWarnings", out _));

        Assert.True(json.RootElement.TryGetProperty("categories", out _));
        Assert.True(json.RootElement.TryGetProperty("byWarningCode", out _));
        Assert.True(json.RootElement.TryGetProperty("byProject", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetWarningTrendsAnalysis_WithMinCount_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetWarningTrendsAnalysis(binlogPath, minCount: 5);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("minCount", out var minCount));
        Assert.Equal(5, minCount.GetInt32());
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

public class TargetDependencyGraphTests
{
    [Fact]
    public void GetTargetDependencyGraph_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetTargetDependencyGraph("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetTargetDependencyGraph_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTargetDependencyGraph(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalTargets", out _));
        Assert.True(summary.TryGetProperty("projectsAnalyzed", out _));
        Assert.True(summary.TryGetProperty("rootTargets", out _));
        Assert.True(summary.TryGetProperty("leafTargets", out _));

        Assert.True(json.RootElement.TryGetProperty("dependencyGraph", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetTargetDependencyGraph_WithProjectFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTargetDependencyGraph(binlogPath, projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectFilter", out var filter));
        Assert.Equal("BinlogMcp", filter.GetString());
    }

    [Fact]
    public void GetTargetDependencyGraph_WithTargetFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTargetDependencyGraph(binlogPath, targetFilter: "Build");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("targetFilter", out var filter));
        Assert.Equal("Build", filter.GetString());
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
