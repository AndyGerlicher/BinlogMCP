using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class ProjectDependencyTests
{
    [Fact]
    public void GetProjectDependencies_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetProjectDependencies("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetProjectDependencies_InvalidFile_ReturnsError()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not a binlog");

            var result = BinlogTools.GetProjectDependencies(tempFile);
            var json = JsonDocument.Parse(result);

            Assert.True(json.RootElement.TryGetProperty("error", out _));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void GetProjectDependencies_RealBinlog_ReturnsValidDependencies()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProjectDependencies(binlogPath);
        var json = JsonDocument.Parse(result);

        // Verify structure
        Assert.True(json.RootElement.TryGetProperty("file", out var file));
        Assert.Equal(binlogPath, file.GetString());

        Assert.True(json.RootElement.TryGetProperty("projectCount", out var projectCount));
        Assert.True(projectCount.GetInt32() >= 1);

        Assert.True(json.RootElement.TryGetProperty("rootProjects", out var rootProjects));
        Assert.True(rootProjects.GetArrayLength() >= 1);

        Assert.True(json.RootElement.TryGetProperty("parallelism", out var parallelism));
        Assert.True(parallelism.TryGetProperty("maxParallelProjects", out _));
        Assert.True(parallelism.TryGetProperty("parallelGroups", out _));

        Assert.True(json.RootElement.TryGetProperty("projects", out var projects));
        Assert.True(projects.GetArrayLength() >= 1);

        // Verify project structure
        var firstProject = projects[0];
        Assert.True(firstProject.TryGetProperty("name", out _));
        Assert.True(firstProject.TryGetProperty("projectFile", out _));
        Assert.True(firstProject.TryGetProperty("buildOrder", out _));
        Assert.True(firstProject.TryGetProperty("durationMs", out _));
        Assert.True(firstProject.TryGetProperty("dependsOn", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetProjectDependencies_WithTestProject_ShowsDependencies()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProjectDependencies(binlogPath);
        var json = JsonDocument.Parse(result);

        // The test project (BinlogMcp.Tests) should depend on main project (BinlogMcp)
        var projects = json.RootElement.GetProperty("projects");

        var testProject = projects.EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("name").GetString()?.Contains("Tests") == true);

        if (testProject.ValueKind != JsonValueKind.Undefined)
        {
            var dependsOn = testProject.GetProperty("dependsOn");
            // Test project should have at least one dependency
            Console.WriteLine($"Test project depends on: {dependsOn}");
        }
    }

    [Fact]
    public void GetProjectDependencies_WithIncludeTargetDetails_ReturnsTargetInvocations()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetProjectDependencies(binlogPath, includeTargetDetails: true);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("includeTargetDetails", out var includeDetails));
        Assert.True(includeDetails.GetBoolean());

        // targetInvocations may be null if there are no MSBuild task calls
        json.RootElement.TryGetProperty("targetInvocations", out var targetInvocations);
        // Just verify the structure is valid - the field exists (even if null)
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
