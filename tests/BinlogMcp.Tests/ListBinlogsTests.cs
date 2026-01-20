using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class ListBinlogsTests : IDisposable
{
    private readonly string _testDir;

    public ListBinlogsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"binlog-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }
    }

    [Fact]
    public void ListBinlogs_DirectoryNotFound_ReturnsError()
    {
        var result = BinlogTools.ListBinlogs("/nonexistent/path");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void ListBinlogs_EmptyDirectory_ReturnsEmptyList()
    {
        var result = BinlogTools.ListBinlogs(_testDir);
        var json = JsonDocument.Parse(result);

        Assert.Equal(0, json.RootElement.GetProperty("count").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("files").EnumerateArray());
    }

    [Fact]
    public void ListBinlogs_FindsBinlogFiles()
    {
        // Create test binlog files
        var binlog1 = Path.Combine(_testDir, "build1.binlog");
        var binlog2 = Path.Combine(_testDir, "build2.binlog");
        File.WriteAllBytes(binlog1, new byte[1024]);
        File.WriteAllBytes(binlog2, new byte[2048]);

        var result = BinlogTools.ListBinlogs(_testDir);
        var json = JsonDocument.Parse(result);

        Assert.Equal(2, json.RootElement.GetProperty("count").GetInt32());

        var files = json.RootElement.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count);

        // Verify file properties exist
        foreach (var file in files)
        {
            Assert.True(file.TryGetProperty("path", out _));
            Assert.True(file.TryGetProperty("fileName", out _));
            Assert.True(file.TryGetProperty("sizeBytes", out _));
            Assert.True(file.TryGetProperty("sizeFormatted", out _));
            Assert.True(file.TryGetProperty("lastModified", out _));
        }
    }

    [Fact]
    public void ListBinlogs_IgnoresNonBinlogFiles()
    {
        File.WriteAllText(Path.Combine(_testDir, "readme.txt"), "test");
        File.WriteAllText(Path.Combine(_testDir, "build.log"), "test");
        File.WriteAllBytes(Path.Combine(_testDir, "actual.binlog"), new byte[100]);

        var result = BinlogTools.ListBinlogs(_testDir);
        var json = JsonDocument.Parse(result);

        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        var fileName = json.RootElement.GetProperty("files")[0].GetProperty("fileName").GetString();
        Assert.Equal("actual.binlog", fileName);
    }

    [Fact]
    public void ListBinlogs_RecursiveSearch_FindsNestedFiles()
    {
        // Create nested directory structure
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);

        File.WriteAllBytes(Path.Combine(_testDir, "root.binlog"), new byte[100]);
        File.WriteAllBytes(Path.Combine(subDir, "nested.binlog"), new byte[100]);

        // Non-recursive should only find root
        var nonRecursiveResult = BinlogTools.ListBinlogs(_testDir, recursive: false);
        var nonRecursiveJson = JsonDocument.Parse(nonRecursiveResult);
        Assert.Equal(1, nonRecursiveJson.RootElement.GetProperty("count").GetInt32());

        // Recursive should find both
        var recursiveResult = BinlogTools.ListBinlogs(_testDir, recursive: true);
        var recursiveJson = JsonDocument.Parse(recursiveResult);
        Assert.Equal(2, recursiveJson.RootElement.GetProperty("count").GetInt32());
    }

    [Fact]
    public void ListBinlogs_FormatsFileSizeCorrectly()
    {
        File.WriteAllBytes(Path.Combine(_testDir, "small.binlog"), new byte[500]);

        var result = BinlogTools.ListBinlogs(_testDir);
        var json = JsonDocument.Parse(result);

        var file = json.RootElement.GetProperty("files")[0];
        Assert.Equal(500, file.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("500 B", file.GetProperty("sizeFormatted").GetString());
    }
}
