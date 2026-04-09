using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class GetPropertiesTests
{
    [Fact]
    public void GetProperties_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetProperties("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetProperties_RealBinlog_ReturnsProperties()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProperties(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("totalProperties", out var total));
        Assert.True(total.GetInt32() > 0);

        Assert.True(json.RootElement.TryGetProperty("properties", out var properties));
        Assert.True(properties.GetArrayLength() > 0);

        Assert.True(json.RootElement.TryGetProperty("importantProperties", out var important));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetProperties_WithFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProperties(binlogPath, nameFilter: "Target");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("properties", out var properties));

        // All returned properties should contain "Target" in the name
        foreach (var prop in properties.EnumerateArray())
        {
            var name = prop.GetProperty("name").GetString();
            Assert.Contains("Target", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetProperties_ReturnsImportantProperties()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProperties(binlogPath);
        var json = JsonDocument.Parse(result);

        var important = json.RootElement.GetProperty("importantProperties");

        // Should find at least Configuration or TargetFramework
        var importantNames = important.EnumerateArray()
            .Select(p => p.GetProperty("name").GetString())
            .ToList();

        Assert.Contains(importantNames, n =>
            n!.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("TargetFramework", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetProperties_WithIncludeOrigin_ReturnsSourceInfo()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProperties(binlogPath, nameFilter: "Configuration", includeOrigin: true);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("includeOrigin", out var includeOrigin));
        Assert.True(includeOrigin.GetBoolean());

        Assert.True(json.RootElement.TryGetProperty("properties", out var properties));
        if (properties.GetArrayLength() > 0)
        {
            var firstProp = properties[0];
            // Should have context field when includeOrigin is true
            Assert.True(firstProp.TryGetProperty("context", out _));
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

public class GetItemsTests
{
    [Fact]
    public void GetItems_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetItems("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetItems_NoItemType_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItems(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("totalItemTypes", out var total));
        Assert.True(total.GetInt32() > 0);

        Assert.True(json.RootElement.TryGetProperty("itemTypes", out var itemTypes));
        Assert.True(itemTypes.GetArrayLength() > 0);

        var firstItemType = itemTypes[0];
        Assert.True(firstItemType.TryGetProperty("itemType", out _));
        Assert.True(firstItemType.TryGetProperty("count", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetItems_SpecificType_ReturnsItems()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        // Try to get Compile items (C# source files)
        var result = BinlogTools.GetItems(binlogPath, itemType: "Compile");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("itemType", out var type));
        Assert.Equal("Compile", type.GetString());

        Assert.True(json.RootElement.TryGetProperty("items", out var items));
        // Should find at least one .cs file in our project
        Console.WriteLine(result);
    }

    [Fact]
    public void GetItems_PackageReference_ReturnsPackages()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetItems(binlogPath, itemType: "PackageReference");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("items", out var items));

        // Our project has NuGet package references
        if (items.GetArrayLength() > 0)
        {
            var firstItem = items[0];
            Assert.True(firstItem.TryGetProperty("include", out _));
        }

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
