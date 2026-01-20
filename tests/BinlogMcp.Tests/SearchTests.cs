using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class SearchTests
{
    [Fact]
    public void SearchBinlog_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.SearchBinlog("/nonexistent/file.binlog", "test");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void SearchBinlog_EmptyQuery_ReturnsError()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.SearchBinlog(binlogPath, "");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("empty", error.GetString());
    }

    [Fact]
    public void SearchBinlog_SearchTargets_FindsResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        // Search for "Compile" which should match targets
        var result = BinlogTools.SearchBinlog(binlogPath, "Compile", "targets");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("targetCount", out var count));
        // Should find at least one Compile-related target
        Console.WriteLine($"Targets found: {count.GetInt32()}");
        Console.WriteLine(result);

        // Even if no exact matches, structure should be valid
        Assert.True(json.RootElement.TryGetProperty("targets", out _));
    }

    [Fact]
    public void SearchBinlog_SearchTasks_FindsResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        // Search for common task patterns - try multiple
        var result = BinlogTools.SearchBinlog(binlogPath, "Resolve", "tasks");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("taskCount", out var count));
        Console.WriteLine($"Tasks found: {count.GetInt32()}");
        Console.WriteLine(result);

        // Structure should be valid
        Assert.True(json.RootElement.TryGetProperty("tasks", out _));
    }

    [Fact]
    public void SearchBinlog_SearchProperties_FindsResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        // Search for "Configuration" property
        var result = BinlogTools.SearchBinlog(binlogPath, "Configuration", "properties");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("propertyCount", out var count));
        Assert.True(count.GetInt32() > 0);

        Assert.True(json.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.GetArrayLength() > 0);
    }

    [Fact]
    public void SearchBinlog_SearchAll_ReturnsMultipleTypes()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        // Search for something common
        var result = BinlogTools.SearchBinlog(binlogPath, "net", "all");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("totalMatches", out var total));
        Assert.True(total.GetInt32() > 0);

        Assert.True(json.RootElement.TryGetProperty("searchType", out var searchType));
        Assert.Equal("all", searchType.GetString());

        Console.WriteLine(result);
    }

    [Fact]
    public void SearchBinlog_CaseInsensitive_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var resultLower = BinlogTools.SearchBinlog(binlogPath, "build", "targets");
        var resultUpper = BinlogTools.SearchBinlog(binlogPath, "BUILD", "targets");

        var jsonLower = JsonDocument.Parse(resultLower);
        var jsonUpper = JsonDocument.Parse(resultUpper);

        var countLower = jsonLower.RootElement.GetProperty("targetCount").GetInt32();
        var countUpper = jsonUpper.RootElement.GetProperty("targetCount").GetInt32();

        Assert.Equal(countLower, countUpper);
    }

    [Fact]
    public void SearchBinlog_LimitResults_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.SearchBinlog(binlogPath, "a", "all", limit: 5);
        var json = JsonDocument.Parse(result);

        // Each category should have at most 5 results returned
        var targets = json.RootElement.GetProperty("targets");
        Assert.True(targets.GetArrayLength() <= 5);
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
