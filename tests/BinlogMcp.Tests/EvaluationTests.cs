using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class GetEvaluatedProjectTests
{
    [Fact]
    public void GetEvaluatedProject_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetEvaluatedProject("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetEvaluatedProject_RealBinlog_ReturnsProjectInfo()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have project section
        Assert.True(json.RootElement.TryGetProperty("project", out var project));
        Assert.True(project.TryGetProperty("name", out _));
        Assert.True(project.TryGetProperty("metadata", out _));

        // Should have summary
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalProperties", out var totalProps));
        Assert.True(totalProps.GetInt32() > 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_ReturnsProperties()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have properties section (categorized)
        Assert.True(json.RootElement.TryGetProperty("properties", out var properties));

        // Properties should be categorized (e.g., framework, build, etc.)
        var propObj = properties.EnumerateObject().ToList();
        Assert.True(propObj.Count > 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_ReturnsItemGroups()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have itemGroups
        Assert.True(json.RootElement.TryGetProperty("itemGroups", out var itemGroups));

        // Should have at least one item type
        var itemGroupsArray = itemGroups.EnumerateArray().ToList();
        Assert.True(itemGroupsArray.Count > 0);

        // Each item group should have itemType and totalCount
        var firstGroup = itemGroupsArray.First();
        Assert.True(firstGroup.TryGetProperty("itemType", out _));
        Assert.True(firstGroup.TryGetProperty("totalCount", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_ReturnsImports()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have imports section (may be empty if imports not recorded in binlog)
        Assert.True(json.RootElement.TryGetProperty("imports", out var imports));

        // If there are imports, verify structure
        var importsArray = imports.EnumerateArray().ToList();
        if (importsArray.Count > 0)
        {
            // Each import should have file and type
            var firstImport = importsArray.First();
            Assert.True(firstImport.TryGetProperty("file", out _));
            Assert.True(firstImport.TryGetProperty("type", out _));
        }

        // Summary should report total imports
        var summary = json.RootElement.GetProperty("summary");
        Assert.True(summary.TryGetProperty("totalImports", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_ReturnsMetadata()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath);
        var json = JsonDocument.Parse(result);

        var project = json.RootElement.GetProperty("project");
        var metadata = project.GetProperty("metadata");

        // Should have key metadata fields
        Assert.True(metadata.TryGetProperty("name", out _));

        // Should have at least targetFramework or configuration
        var hasFramework = metadata.TryGetProperty("targetFramework", out var tf) && tf.GetString() != null;
        var hasConfig = metadata.TryGetProperty("configuration", out var cfg) && cfg.GetString() != null;
        Assert.True(hasFramework || hasConfig);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_WithProjectFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath, projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        // Should return project matching filter
        Assert.True(json.RootElement.TryGetProperty("project", out var project));
        var name = project.GetProperty("name").GetString();
        Assert.Contains("BinlogMcp", name, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_NonexistentProject_ReturnsError()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath, projectFilter: "NonexistentProject12345");
        var json = JsonDocument.Parse(result);

        // Should return error
        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetEvaluatedProject_IncludeAllProperties_ReturnsMoreProperties()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var defaultResult = BinlogTools.GetEvaluatedProject(binlogPath, includeAllProperties: false);
        var allResult = BinlogTools.GetEvaluatedProject(binlogPath, includeAllProperties: true);

        var defaultJson = JsonDocument.Parse(defaultResult);
        var allJson = JsonDocument.Parse(allResult);

        var defaultShown = defaultJson.RootElement.GetProperty("summary").GetProperty("shownProperties").GetInt32();
        var allShown = allJson.RootElement.GetProperty("summary").GetProperty("shownProperties").GetInt32();

        // includeAllProperties should show more properties
        Assert.True(allShown >= defaultShown);

        Console.WriteLine($"Default: {defaultShown} properties, All: {allShown} properties");
    }

    [Fact]
    public void GetEvaluatedProject_IncludeItemMetadata_ReturnsMetadata()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath, includeItemMetadata: true);
        var json = JsonDocument.Parse(result);

        var itemGroups = json.RootElement.GetProperty("itemGroups").EnumerateArray().ToList();

        // Find an item group with items
        foreach (var group in itemGroups)
        {
            var items = group.GetProperty("items");
            if (items.GetArrayLength() > 0)
            {
                var firstItem = items[0];
                // With includeItemMetadata, items should be objects with include and potentially metadata
                Assert.True(firstItem.TryGetProperty("include", out _));
                break;
            }
        }

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_MarkdownFormat_ReturnsMarkdown()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath, format: "markdown");

        // Should be markdown, not JSON
        Assert.Contains("# Evaluated Project", result);
        Assert.Contains("##", result);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetEvaluatedProject_InvalidFormat_ReturnsError()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetEvaluatedProject(binlogPath, format: "invalid");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("Invalid format", error.GetString());
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
