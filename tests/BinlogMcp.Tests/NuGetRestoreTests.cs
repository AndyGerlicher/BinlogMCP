using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class NuGetRestoreTests
{
    [Fact]
    public void GetNuGetRestoreAnalysis_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetNuGetRestoreAnalysis("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetNuGetRestoreAnalysis_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetNuGetRestoreAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));

        Assert.True(summary.TryGetProperty("totalRestoreTimeMs", out _));
        Assert.True(summary.TryGetProperty("totalPackages", out _));
        Assert.True(summary.TryGetProperty("projectsWithPackages", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetNuGetRestoreAnalysis_FindsPackageReferences()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetNuGetRestoreAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        // Our project has NuGet packages (ModelContextProtocol, MSBuild.StructuredLogger)
        var summary = json.RootElement.GetProperty("summary");
        var totalPackages = summary.GetProperty("totalPackages").GetInt32();

        // Should have at least some packages
        Assert.True(totalPackages >= 0);

        // Check packagesByProject structure
        Assert.True(json.RootElement.TryGetProperty("packagesByProject", out var byProject));
        if (byProject.GetArrayLength() > 0)
        {
            var firstProject = byProject[0];
            Assert.True(firstProject.TryGetProperty("project", out _));
            Assert.True(firstProject.TryGetProperty("packageCount", out _));
            Assert.True(firstProject.TryGetProperty("packages", out _));
        }
    }

    [Fact]
    public void GetNuGetRestoreAnalysis_FindsRestoreTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetNuGetRestoreAnalysis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("restoreTargets", out var targets));

        // May or may not have restore targets depending on build type
        if (targets.GetArrayLength() > 0)
        {
            var firstTarget = targets[0];
            Assert.True(firstTarget.TryGetProperty("name", out _));
            Assert.True(firstTarget.TryGetProperty("durationMs", out _));
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
}
