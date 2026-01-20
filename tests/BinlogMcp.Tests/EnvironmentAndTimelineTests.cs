using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class EnvironmentVariablesTests
{
    [Fact]
    public void GetEnvironmentVariables_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetEnvironmentVariables("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetEnvironmentVariables_RealBinlog_ReturnsVariables()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEnvironmentVariables(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("count", out _));
        Assert.True(json.RootElement.TryGetProperty("variables", out var variables));
        Assert.Equal(JsonValueKind.Array, variables.ValueKind);
    }

    [Fact]
    public void GetEnvironmentVariables_WithFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEnvironmentVariables(binlogPath, filter: "DOTNET");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("filter", out var filter));
        Assert.Equal("DOTNET", filter.GetString());
    }

    [Fact]
    public void GetEnvironmentVariables_MsbuildOnlyFlag_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEnvironmentVariables(binlogPath, msbuildOnly: true);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("msbuildOnly", out var flag));
        Assert.True(flag.GetBoolean());
    }

    [Fact]
    public void GetEnvironmentVariables_MarkdownFormat_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEnvironmentVariables(binlogPath, format: "markdown");
        Assert.Contains("#", result); // Should contain markdown headers
    }

    [Fact]
    public void GetEnvironmentVariables_CsvFormat_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEnvironmentVariables(binlogPath, format: "csv");
        Assert.Contains(",", result); // Should contain CSV delimiters
    }

    [Fact]
    public void GetEnvironmentVariables_InvalidFormat_ReturnsError()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEnvironmentVariables(binlogPath, format: "invalid");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
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

public class TimelineTests
{
    [Fact]
    public void GetTimeline_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetTimeline("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetTimeline_RealBinlog_ReturnsTimeline()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTimeline(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("level", out var level));
        Assert.Equal("targets", level.GetString());
        Assert.True(json.RootElement.TryGetProperty("buildStartTime", out _));
        Assert.True(json.RootElement.TryGetProperty("buildDurationMs", out _));
        Assert.True(json.RootElement.TryGetProperty("eventCount", out _));
        Assert.True(json.RootElement.TryGetProperty("events", out var events));
        Assert.Equal(JsonValueKind.Array, events.ValueKind);
    }

    [Fact]
    public void GetTimeline_ProjectsLevel_ReturnsProjectsOnly()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTimeline(binlogPath, level: "projects");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("level", out var level));
        Assert.Equal("projects", level.GetString());

        // Projects level shouldn't have children (targets)
        Assert.True(json.RootElement.TryGetProperty("events", out var events));
        if (events.GetArrayLength() > 0)
        {
            var firstEvent = events[0];
            // Property names are PascalCase (Type, Name, etc.)
            Assert.True(firstEvent.TryGetProperty("Type", out var type));
            Assert.Equal("project", type.GetString());
        }
    }

    [Fact]
    public void GetTimeline_TasksLevel_IncludesTasks()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTimeline(binlogPath, level: "tasks");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("level", out var level));
        Assert.Equal("tasks", level.GetString());
    }

    [Fact]
    public void GetTimeline_WithMinDuration_FiltersByDuration()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var resultAll = BinlogTools.GetTimeline(binlogPath, minDurationMs: 0);
        var jsonAll = JsonDocument.Parse(resultAll);

        var resultFiltered = BinlogTools.GetTimeline(binlogPath, minDurationMs: 100);
        var jsonFiltered = JsonDocument.Parse(resultFiltered);

        var countAll = jsonAll.RootElement.GetProperty("eventCount").GetInt32();
        var countFiltered = jsonFiltered.RootElement.GetProperty("eventCount").GetInt32();

        // Filtered should have same or fewer events
        Assert.True(countFiltered <= countAll);
    }

    [Fact]
    public void GetTimeline_WithProjectFilter_FiltersProjects()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTimeline(binlogPath, projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("events", out var events));
        // All events should be for BinlogMcp project (Name is PascalCase)
        foreach (var ev in events.EnumerateArray())
        {
            Assert.True(ev.TryGetProperty("Name", out var name));
            Assert.Contains("BinlogMcp", name.GetString());
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

public class ItemMetadataTests
{
    [Fact]
    public void GetItemMetadata_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetItemMetadata("/nonexistent/file.binlog", "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetItemMetadata_EmptyItemType_ReturnsError()
    {
        var result = BinlogTools.GetItemMetadata("/any/path.binlog", "");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetItemMetadata_CompileItems_ReturnsMetadata()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItemMetadata(binlogPath, "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("itemType", out var itemType));
        Assert.Equal("Compile", itemType.GetString());
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalItems", out _));
        Assert.True(json.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
    }

    [Fact]
    public void GetItemMetadata_PackageReference_ReturnsVersionInfo()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItemMetadata(binlogPath, "PackageReference");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("itemType", out var itemType));
        Assert.Equal("PackageReference", itemType.GetString());
        Assert.True(json.RootElement.TryGetProperty("metadataSummary", out var metadataSummary));
        Assert.Equal(JsonValueKind.Array, metadataSummary.ValueKind);
    }

    [Fact]
    public void GetItemMetadata_WithValueFilter_FiltersItems()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItemMetadata(binlogPath, "Compile", valueFilter: "Program");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("valueFilter", out var filter));
        Assert.Equal("Program", filter.GetString());
    }

    [Fact]
    public void GetItemMetadata_WithLimit_RespectsLimit()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItemMetadata(binlogPath, "Compile", limit: 5);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("items", out var items));
        Assert.True(items.GetArrayLength() <= 5);
    }

    [Fact]
    public void GetItemMetadata_MarkdownFormat_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItemMetadata(binlogPath, "Compile", format: "markdown");
        Assert.Contains("#", result);
    }

    [Fact]
    public void GetItemMetadata_CsvFormat_Works()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItemMetadata(binlogPath, "Compile", format: "csv");
        Assert.Contains(",", result);
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

public class TargetInputsOutputsTests
{
    [Fact]
    public void GetTargetInputsOutputs_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetTargetInputsOutputs("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetTargetInputsOutputs_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTargetInputsOutputs(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalTargets", out _));
        Assert.True(summary.TryGetProperty("executed", out _));
        Assert.True(summary.TryGetProperty("skipped", out _));
        Assert.True(json.RootElement.TryGetProperty("targets", out var targets));
        Assert.Equal(JsonValueKind.Array, targets.ValueKind);
    }

    [Fact]
    public void GetTargetInputsOutputs_WithTargetFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTargetInputsOutputs(binlogPath, targetFilter: "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("targetFilter", out var filter));
        Assert.Equal("Compile", filter.GetString());
    }

    [Fact]
    public void GetTargetInputsOutputs_ExecutedOnly_FiltersSkipped()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var resultAll = BinlogTools.GetTargetInputsOutputs(binlogPath, executedOnly: false);
        var jsonAll = JsonDocument.Parse(resultAll);

        var resultExecuted = BinlogTools.GetTargetInputsOutputs(binlogPath, executedOnly: true);
        var jsonExecuted = JsonDocument.Parse(resultExecuted);

        var countAll = jsonAll.RootElement.GetProperty("summary").GetProperty("totalTargets").GetInt32();
        var countExecuted = jsonExecuted.RootElement.GetProperty("summary").GetProperty("totalTargets").GetInt32();

        // Executed only should have same or fewer targets
        Assert.True(countExecuted <= countAll);
    }

    [Fact]
    public void GetTargetInputsOutputs_WithLimit_RespectsLimit()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTargetInputsOutputs(binlogPath, limit: 10);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("targets", out var targets));
        Assert.True(targets.GetArrayLength() <= 10);
    }

    [Fact]
    public void GetTargetInputsOutputs_TargetHasExpectedFields()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetTargetInputsOutputs(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("targets", out var targets));
        if (targets.GetArrayLength() > 0)
        {
            var firstTarget = targets[0];
            Assert.True(firstTarget.TryGetProperty("target", out _));
            Assert.True(firstTarget.TryGetProperty("project", out _));
            Assert.True(firstTarget.TryGetProperty("durationMs", out _));
            Assert.True(firstTarget.TryGetProperty("succeeded", out _));
            Assert.True(firstTarget.TryGetProperty("wasSkipped", out _));
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
