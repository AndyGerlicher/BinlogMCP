using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class GetBuildSummaryTests
{
    [Fact]
    public void GetBuildSummary_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetBuildSummary("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetBuildSummary_InvalidFile_ReturnsError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not a binlog");

            var result = BinlogTools.GetBuildSummary(tempFile);
            var json = JsonDocument.Parse(result);

            Assert.True(json.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetBuildSummary_RealBinlog_ReturnsValidSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
        {
            // Skip if no binlog found
            return;
        }

        var result = BinlogTools.GetBuildSummary(binlogPath);
        var json = JsonDocument.Parse(result);

        // Verify structure
        Assert.True(json.RootElement.TryGetProperty("succeeded", out var succeeded));
        Assert.True(succeeded.GetBoolean()); // Our test build should succeed

        Assert.True(json.RootElement.TryGetProperty("duration", out var duration));
        Assert.True(duration.TryGetProperty("totalSeconds", out _));
        Assert.True(duration.TryGetProperty("formatted", out _));

        Assert.True(json.RootElement.TryGetProperty("errorCount", out var errorCount));
        Assert.Equal(0, errorCount.GetInt32()); // Successful build = no errors

        Assert.True(json.RootElement.TryGetProperty("warningCount", out _));
        Assert.True(json.RootElement.TryGetProperty("projectCount", out _));
        Assert.True(json.RootElement.TryGetProperty("projects", out var projects));
        Assert.True(projects.GetArrayLength() > 0);

        // Output for inspection
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
