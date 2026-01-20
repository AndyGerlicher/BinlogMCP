using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class GetErrorsTests
{
    [Fact]
    public void GetErrors_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetErrors("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetErrors_InvalidFile_ReturnsError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not a binlog");
            var result = BinlogTools.GetErrors(tempFile);
            var json = JsonDocument.Parse(result);

            Assert.True(json.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetErrors_RealBinlog_ReturnsValidStructure()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetErrors(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("count", out var count));
        Assert.Equal(0, count.GetInt32()); // Our successful build has no errors

        Assert.True(json.RootElement.TryGetProperty("errors", out var errors));
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);

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

public class GetWarningsTests
{
    [Fact]
    public void GetWarnings_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetWarnings("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetWarnings_InvalidFile_ReturnsError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not a binlog");
            var result = BinlogTools.GetWarnings(tempFile);
            var json = JsonDocument.Parse(result);

            Assert.True(json.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetWarnings_RealBinlog_ReturnsValidStructure()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetWarnings(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("count", out _));
        Assert.True(json.RootElement.TryGetProperty("warnings", out var warnings));
        Assert.Equal(JsonValueKind.Array, warnings.ValueKind);

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
