using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class ErrorBinlogTests
{
    [Fact]
    public void GetErrors_BinlogWithErrors_ReturnsErrorDetails()
    {
        var binlogPath = FindErrorBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetErrors(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("count", out var count));
        Assert.True(count.GetInt32() > 0);

        var errors = json.RootElement.GetProperty("errors");
        var firstError = errors[0];

        // Verify error structure
        Assert.True(firstError.TryGetProperty("message", out _));
        Assert.True(firstError.TryGetProperty("code", out var code));
        Assert.Equal("CS1002", code.GetString());
        Assert.True(firstError.TryGetProperty("file", out _));
        Assert.True(firstError.TryGetProperty("lineNumber", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetBuildSummary_BinlogWithErrors_ShowsFailure()
    {
        var binlogPath = FindErrorBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetBuildSummary(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.False(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.True(json.RootElement.GetProperty("errorCount").GetInt32() > 0);

        Console.WriteLine(result);
    }

    private static string? FindErrorBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test-data", "error.binlog");
            if (File.Exists(binlog)) return binlog;
            dir = dir.Parent;
        }
        return null;
    }
}
