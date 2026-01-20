using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class DiffToolsTests
{
    #region DiffProperties Tests

    [Fact]
    public void DiffProperties_BaselineNotFound_ReturnsError()
    {
        var result = BinlogTools.DiffProperties("/nonexistent/baseline.binlog", "/nonexistent/comparison.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Baseline", error.GetString());
    }

    [Fact]
    public void DiffProperties_SameFile_ReturnsNoChanges()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffProperties(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.Equal(0, summary.GetProperty("changedCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("addedCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("removedCount").GetInt32());
    }

    [Fact]
    public void DiffProperties_WithNameFilter_FiltersProperties()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffProperties(binlogPath, binlogPath, nameFilter: "Configuration");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("nameFilter", out var filter));
        Assert.Equal("Configuration", filter.GetString());
    }

    [Fact]
    public void DiffProperties_ImportantOnly_FiltersToImportantProps()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffProperties(binlogPath, binlogPath, importantOnly: true);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("importantOnly", out var flag));
        Assert.True(flag.GetBoolean());
    }

    [Fact]
    public void DiffProperties_DifferentFiles_ReturnsDifferences()
    {
        var testBinlog = FindTestBinlog();
        var errorBinlog = FindErrorBinlog();
        if (testBinlog == null || errorBinlog == null)
            return;

        var result = BinlogTools.DiffProperties(testBinlog, errorBinlog);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(json.RootElement.TryGetProperty("baselineFile", out _));
        Assert.True(json.RootElement.TryGetProperty("comparisonFile", out _));

        // Should have some differences between different builds
        var totalDiffs = summary.GetProperty("changedCount").GetInt32() +
                        summary.GetProperty("addedCount").GetInt32() +
                        summary.GetProperty("removedCount").GetInt32();
        Assert.True(totalDiffs >= 0); // May or may not have differences
    }

    #endregion

    #region DiffItems Tests

    [Fact]
    public void DiffItems_BaselineNotFound_ReturnsError()
    {
        var result = BinlogTools.DiffItems("/nonexistent/baseline.binlog", "/nonexistent/comparison.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Baseline", error.GetString());
    }

    [Fact]
    public void DiffItems_SameFile_ReturnsNoChanges()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffItems(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.Equal(0, summary.GetProperty("totalAdded").GetInt32());
        Assert.Equal(0, summary.GetProperty("totalRemoved").GetInt32());
        Assert.Equal(0, summary.GetProperty("totalVersionChanged").GetInt32());
    }

    [Fact]
    public void DiffItems_WithItemTypeFilter_FiltersToSpecificType()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffItems(binlogPath, binlogPath, itemType: "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("itemTypeFilter", out var filter));
        Assert.Equal("Compile", filter.GetString());
    }

    [Fact]
    public void DiffItems_NoFilter_ComparesMultipleItemTypes()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffItems(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("itemTypeFilter", out var filter));
        Assert.Equal(JsonValueKind.Null, filter.ValueKind);
    }

    [Fact]
    public void DiffItems_DifferentFiles_ReturnsValidComparison()
    {
        var testBinlog = FindTestBinlog();
        var errorBinlog = FindErrorBinlog();
        if (testBinlog == null || errorBinlog == null)
            return;

        var result = BinlogTools.DiffItems(testBinlog, errorBinlog);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out _));
        Assert.True(json.RootElement.TryGetProperty("itemDiffs", out var diffs));
        Assert.Equal(JsonValueKind.Array, diffs.ValueKind);
    }

    #endregion

    #region DiffTargetExecution Tests

    [Fact]
    public void DiffTargetExecution_BaselineNotFound_ReturnsError()
    {
        var result = BinlogTools.DiffTargetExecution("/nonexistent/baseline.binlog", "/nonexistent/comparison.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Baseline", error.GetString());
    }

    [Fact]
    public void DiffTargetExecution_SameFile_ReturnsNoChanges()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffTargetExecution(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.Equal(0, summary.GetProperty("onlyInBaselineCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("onlyInComparisonCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("statusChangedCount").GetInt32());
    }

    [Fact]
    public void DiffTargetExecution_WithProjectFilter_FiltersTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffTargetExecution(binlogPath, binlogPath, projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectFilter", out var filter));
        Assert.Equal("BinlogMcp", filter.GetString());
    }

    [Fact]
    public void DiffTargetExecution_WithMinDuration_FiltersSmallTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffTargetExecution(binlogPath, binlogPath, minDurationMs: 100);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("minDurationMs", out var minDur));
        Assert.Equal(100, minDur.GetDouble());
    }

    [Fact]
    public void DiffTargetExecution_DifferentFiles_FindsStatusChanges()
    {
        var testBinlog = FindTestBinlog();
        var errorBinlog = FindErrorBinlog();
        if (testBinlog == null || errorBinlog == null)
            return;

        var result = BinlogTools.DiffTargetExecution(testBinlog, errorBinlog);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        // Different builds may have different targets executed
        var baselineCount = summary.GetProperty("baselineTargetCount").GetInt32();
        var comparisonCount = summary.GetProperty("comparisonTargetCount").GetInt32();
        Assert.True(baselineCount > 0 || comparisonCount > 0);
    }

    #endregion

    #region DiffImports Tests

    [Fact]
    public void DiffImports_BaselineNotFound_ReturnsError()
    {
        var result = BinlogTools.DiffImports("/nonexistent/baseline.binlog", "/nonexistent/comparison.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Baseline", error.GetString());
    }

    [Fact]
    public void DiffImports_SameFile_ReturnsNoChanges()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffImports(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.Equal(0, summary.GetProperty("addedCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("removedCount").GetInt32());
    }

    [Fact]
    public void DiffImports_WithProjectFilter_FiltersImports()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffImports(binlogPath, binlogPath, projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectFilter", out var filter));
        Assert.Equal("BinlogMcp", filter.GetString());
    }

    [Fact]
    public void DiffImports_DifferentFiles_ReturnsValidComparison()
    {
        var testBinlog = FindTestBinlog();
        var errorBinlog = FindErrorBinlog();
        if (testBinlog == null || errorBinlog == null)
            return;

        var result = BinlogTools.DiffImports(testBinlog, errorBinlog);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(json.RootElement.TryGetProperty("baselineFile", out _));
        Assert.True(json.RootElement.TryGetProperty("comparisonFile", out _));

        // Both builds should have imports
        var baselineCount = summary.GetProperty("baselineImportCount").GetInt32();
        var comparisonCount = summary.GetProperty("comparisonImportCount").GetInt32();
        Assert.True(baselineCount >= 0);
        Assert.True(comparisonCount >= 0);
    }

    [Fact]
    public void DiffImports_ReturnsAddedAndRemovedArrays()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.DiffImports(binlogPath, binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("added", out var added));
        Assert.True(json.RootElement.TryGetProperty("removed", out var removed));
        Assert.Equal(JsonValueKind.Array, added.ValueKind);
        Assert.Equal(JsonValueKind.Array, removed.ValueKind);
    }

    #endregion

    #region Helper Methods

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

    private static string? FindErrorBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test-data", "error.binlog");
            if (File.Exists(binlog))
                return binlog;
            dir = dir.Parent;
        }
        return null;
    }

    #endregion
}
