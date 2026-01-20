using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class AssemblyReferenceTests
{
    [Fact]
    public void GetAssemblyReferences_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetAssemblyReferences("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetAssemblyReferences_RealBinlog_ReturnsSummary()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetAssemblyReferences(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("file", out _));
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));

        Assert.True(summary.TryGetProperty("totalReferences", out _));
        Assert.True(summary.TryGetProperty("uniqueAssemblies", out _));
        Assert.True(summary.TryGetProperty("projectReferences", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetAssemblyReferences_ReturnsReferencesByProject()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetAssemblyReferences(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("referencesByProject", out var byProject));

        if (byProject.GetArrayLength() > 0)
        {
            var firstProject = byProject[0];
            Assert.True(firstProject.TryGetProperty("project", out _));
            Assert.True(firstProject.TryGetProperty("referenceCount", out _));
            Assert.True(firstProject.TryGetProperty("references", out _));
        }
    }

    [Fact]
    public void GetAssemblyReferences_ReturnsProjectReferences()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetAssemblyReferences(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectReferences", out var projRefs));

        // Our test project (BinlogMcp.Tests) references BinlogMcp
        if (projRefs.GetArrayLength() > 0)
        {
            var firstRef = projRefs[0];
            Assert.True(firstRef.TryGetProperty("referencedProject", out _));
            Assert.True(firstRef.TryGetProperty("fromProject", out _));
        }
    }

    [Fact]
    public void GetAssemblyReferences_WithFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetAssemblyReferences(binlogPath, projectFilter: "Tests");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("projectFilter", out var filter));
        Assert.Equal("Tests", filter.GetString());

        // If there are results, they should be from the Tests project
        var byProject = json.RootElement.GetProperty("referencesByProject");
        foreach (var proj in byProject.EnumerateArray())
        {
            var projectName = proj.GetProperty("project").GetString();
            Assert.Contains("Tests", projectName, StringComparison.OrdinalIgnoreCase);
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
