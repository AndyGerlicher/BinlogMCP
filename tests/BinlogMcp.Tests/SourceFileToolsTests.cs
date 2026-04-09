using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class SourceFileToolsTests
{
    [Fact]
    public void ListEmbeddedSourceFiles_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.ListEmbeddedSourceFiles("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void ListEmbeddedSourceFiles_RealBinlog_ReturnsValidResult()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.ListEmbeddedSourceFiles(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have fileCount property
        Assert.True(json.RootElement.TryGetProperty("fileCount", out var fileCount));
        // Whether there are embedded files depends on the test binlog,
        // but the structure should be valid
        Assert.True(fileCount.GetInt32() >= 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void ListEmbeddedSourceFiles_WithFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.ListEmbeddedSourceFiles(binlogPath, ".csproj");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("fileCount", out _));
        Assert.True(json.RootElement.TryGetProperty("filtered", out var filtered));
        Assert.True(filtered.GetBoolean());

        // If there are results, they should all contain .csproj
        if (json.RootElement.TryGetProperty("files", out var files))
        {
            foreach (var file in files.EnumerateArray())
            {
                var path = file.GetProperty("path").GetString();
                Assert.Contains(".csproj", path, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void GetEmbeddedSourceFile_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetEmbeddedSourceFile("/nonexistent/file.binlog", "some.csproj");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetEmbeddedSourceFile_NonexistentFile_ReturnsSuggestions()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        // First check if the binlog has any source files
        var listResult = BinlogTools.ListEmbeddedSourceFiles(binlogPath);
        var listJson = JsonDocument.Parse(listResult);
        var fileCount = listJson.RootElement.GetProperty("fileCount").GetInt32();

        if (fileCount == 0) return; // No embedded files to test

        var result = BinlogTools.GetEmbeddedSourceFile(binlogPath, "nonexistent_file_that_does_not_exist.xyz");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetEmbeddedSourceFile_ExistingFile_ReturnsContent()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        // List files and pick the first one
        var listResult = BinlogTools.ListEmbeddedSourceFiles(binlogPath);
        var listJson = JsonDocument.Parse(listResult);
        var fileCount = listJson.RootElement.GetProperty("fileCount").GetInt32();

        if (fileCount == 0) return; // No embedded files to test

        var firstFile = listJson.RootElement.GetProperty("files").EnumerateArray().First();
        var filePath = firstFile.GetProperty("path").GetString()!;

        var result = BinlogTools.GetEmbeddedSourceFile(binlogPath, filePath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("path", out _));
        Assert.True(json.RootElement.TryGetProperty("content", out var content));
        Assert.NotNull(content.GetString());
        Assert.True(content.GetString()!.Length > 0);
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
