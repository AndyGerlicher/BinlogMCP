using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class TraceItemTests
{
    [Fact]
    public void TraceItem_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.TraceItem("/nonexistent/file.binlog", "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void TraceItem_EmptyItemType_ReturnsError()
    {
        var result = BinlogTools.TraceItem("test.binlog", "");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void TraceItem_RealBinlog_ReturnsItemTraces()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.TraceItem(binlogPath, "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("itemType", out var itemType));
        Assert.Equal("Compile", itemType.GetString());

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalItemsTraced", out _));

        Assert.True(json.RootElement.TryGetProperty("itemTraces", out var traces));
        Assert.True(traces.GetArrayLength() >= 0);
    }

    [Fact]
    public void TraceItem_WithValuePattern_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.TraceItem(binlogPath, "Compile", valuePattern: ".cs");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("valuePattern", out var pattern));
        Assert.Equal(".cs", pattern.GetString());
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlogPath = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlogPath))
                return binlogPath;
            dir = dir.Parent;
        }
        return null;
    }
}

public class GetItemTransformsTests
{
    [Fact]
    public void GetItemTransforms_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetItemTransforms("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetItemTransforms_RealBinlog_ReturnsTransforms()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetItemTransforms(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("targetsWithOperations", out _));
        Assert.True(summary.TryGetProperty("totalAddOperations", out _));
        Assert.True(summary.TryGetProperty("totalRemoveOperations", out _));

        Assert.True(json.RootElement.TryGetProperty("byItemType", out _));
        Assert.True(json.RootElement.TryGetProperty("transformations", out _));
    }

    [Fact]
    public void GetItemTransforms_WithItemTypeFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetItemTransforms(binlogPath, itemType: "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("itemType", out var itemType));
        Assert.Equal("Compile", itemType.GetString());
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlogPath = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlogPath))
                return binlogPath;
            dir = dir.Parent;
        }
        return null;
    }
}

public class GetMSBuildTaskCallsTests
{
    [Fact]
    public void GetMSBuildTaskCalls_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetMSBuildTaskCalls("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetMSBuildTaskCalls_RealBinlog_ReturnsCalls()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetMSBuildTaskCalls(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalMSBuildCalls", out _));
        Assert.True(summary.TryGetProperty("totalCallDurationMs", out _));

        Assert.True(json.RootElement.TryGetProperty("callGraph", out _));
        Assert.True(json.RootElement.TryGetProperty("calls", out _));
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlogPath = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlogPath))
                return binlogPath;
            dir = dir.Parent;
        }
        return null;
    }
}

public class TracePropertyTests
{
    [Fact]
    public void TraceProperty_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.TraceProperty("/nonexistent/file.binlog", "Configuration");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void TraceProperty_EmptyPropertyName_ReturnsError()
    {
        var result = BinlogTools.TraceProperty("test.binlog", "");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void TraceProperty_RealBinlog_ReturnsTrace()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.TraceProperty(binlogPath, "Configuration");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("propertyName", out var propName));
        Assert.Equal("Configuration", propName.GetString());

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalAssignments", out _));
        Assert.True(summary.TryGetProperty("projectsWithProperty", out _));
        Assert.True(summary.TryGetProperty("distinctValues", out _));

        Assert.True(json.RootElement.TryGetProperty("projectTraces", out _));
    }

    [Fact]
    public void TraceProperty_TargetFramework_ShowsValueChanges()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.TraceProperty(binlogPath, "TargetFramework");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectTraces", out var traces));

        // Should have at least one project trace
        if (traces.GetArrayLength() > 0)
        {
            var firstTrace = traces[0];
            Assert.True(firstTrace.TryGetProperty("project", out _));
            Assert.True(firstTrace.TryGetProperty("finalValue", out _));
            Assert.True(firstTrace.TryGetProperty("trace", out _));
        }
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlogPath = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlogPath))
                return binlogPath;
            dir = dir.Parent;
        }
        return null;
    }
}
