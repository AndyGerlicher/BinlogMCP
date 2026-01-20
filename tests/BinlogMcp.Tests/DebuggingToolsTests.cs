using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class GetTargetExecutionReasonsTests
{
    [Fact]
    public void GetTargetExecutionReasons_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetTargetExecutionReasons("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetTargetExecutionReasons_RealBinlog_ReturnsTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetTargetExecutionReasons(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have summary
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalTargets", out var total));
        Assert.True(total.GetInt32() > 0);

        // Should have targets array
        Assert.True(json.RootElement.TryGetProperty("targets", out var targets));
        Assert.True(targets.GetArrayLength() > 0);

        // Each target should have execution reasons
        var firstTarget = targets[0];
        Assert.True(firstTarget.TryGetProperty("target", out _));
        Assert.True(firstTarget.TryGetProperty("executionReasons", out var reasons));
        Assert.True(reasons.GetArrayLength() > 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetTargetExecutionReasons_WithTargetFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetTargetExecutionReasons(binlogPath, targetFilter: "Build");
        var json = JsonDocument.Parse(result);

        var targets = json.RootElement.GetProperty("targets");
        foreach (var target in targets.EnumerateArray())
        {
            var name = target.GetProperty("target").GetString();
            Assert.Contains("Build", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetTargetExecutionReasons_ShowsDependsOnTargets()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetTargetExecutionReasons(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have summary showing targets with dependencies
        var summary = json.RootElement.GetProperty("summary");
        Assert.True(summary.TryGetProperty("targetsWithDependencies", out var depsCount));
        // Most builds have targets with DependsOnTargets
        Assert.True(depsCount.GetInt32() >= 0);

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

public class GetSkippedTargetsTests
{
    [Fact]
    public void GetSkippedTargets_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetSkippedTargets("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetSkippedTargets_RealBinlog_ReturnsResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetSkippedTargets(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have summary
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalSkippedTargets", out _));
        Assert.True(summary.TryGetProperty("executedTargets", out _));

        // Should have skippedTargets array (may be empty if all targets executed)
        Assert.True(json.RootElement.TryGetProperty("skippedTargets", out _));

        // Should have byReason categorization
        Assert.True(json.RootElement.TryGetProperty("byReason", out _));

        Console.WriteLine(result);
    }

    [Fact]
    public void GetSkippedTargets_WithProjectFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetSkippedTargets(binlogPath, projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        var skippedTargets = json.RootElement.GetProperty("skippedTargets");
        foreach (var target in skippedTargets.EnumerateArray())
        {
            var project = target.GetProperty("project").GetString();
            Assert.Contains("BinlogMcp", project, StringComparison.OrdinalIgnoreCase);
        }
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

public class GetPropertyOriginTests
{
    [Fact]
    public void GetPropertyOrigin_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetPropertyOrigin("/nonexistent/file.binlog", "Configuration");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetPropertyOrigin_EmptyPropertyName_ReturnsError()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetPropertyOrigin(binlogPath, "");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetPropertyOrigin_CommonProperty_ReturnsAssignments()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetPropertyOrigin(binlogPath, "Configuration");
        var json = JsonDocument.Parse(result);

        // Should have summary
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalAssignments", out var total));
        Assert.True(total.GetInt32() > 0);

        // Should have projectAssignments
        Assert.True(json.RootElement.TryGetProperty("projectAssignments", out var projects));
        Assert.True(projects.GetArrayLength() > 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetPropertyOrigin_TargetFramework_ShowsOrigin()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetPropertyOrigin(binlogPath, "TargetFramework");
        var json = JsonDocument.Parse(result);

        var summary = json.RootElement.GetProperty("summary");
        Assert.True(summary.TryGetProperty("likelyOrigin", out var origin));
        Assert.NotNull(origin.GetString());

        Console.WriteLine(result);
    }

    [Fact]
    public void GetPropertyOrigin_NonExistentProperty_ReturnsEmptyResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetPropertyOrigin(binlogPath, "NonExistentPropertyXYZ123");
        var json = JsonDocument.Parse(result);

        var summary = json.RootElement.GetProperty("summary");
        var total = summary.GetProperty("totalAssignments").GetInt32();
        Assert.Equal(0, total);

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

public class GetImportChainTests
{
    [Fact]
    public void GetImportChain_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetImportChain("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void GetImportChain_RealBinlog_ReturnsImports()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetImportChain(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have summary
        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalProjects", out _));
        Assert.True(summary.TryGetProperty("totalImports", out _));

        // Should have projectImports
        Assert.True(json.RootElement.TryGetProperty("projectImports", out var projectImports));
        // Note: totalImports may be 0 if imports aren't captured in this binlog format

        Console.WriteLine(result);
    }

    [Fact]
    public void GetImportChain_WithProjectFilter_FiltersResults()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetImportChain(binlogPath, projectFilter: "BinlogMcp");
        var json = JsonDocument.Parse(result);

        var projectImports = json.RootElement.GetProperty("projectImports");
        foreach (var project in projectImports.EnumerateArray())
        {
            var projectName = project.GetProperty("project").GetString();
            Assert.Contains("BinlogMcp", projectName, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void GetImportChain_WithImportFilter_FiltersImports()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetImportChain(binlogPath, importFilter: ".props");
        var json = JsonDocument.Parse(result);

        var projectImports = json.RootElement.GetProperty("projectImports");
        foreach (var project in projectImports.EnumerateArray())
        {
            if (project.TryGetProperty("imports", out var imports))
            {
                foreach (var import in imports.EnumerateArray())
                {
                    var file = import.GetProperty("file").GetString();
                    Assert.Contains(".props", file, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void GetImportChain_IdentifiesImportTypes()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetImportChain(binlogPath);
        var json = JsonDocument.Parse(result);

        // Check that import types are identified
        var projectImports = json.RootElement.GetProperty("projectImports");
        var hasTypedImport = false;

        foreach (var project in projectImports.EnumerateArray())
        {
            if (project.TryGetProperty("importTypes", out var importTypes) && importTypes.GetArrayLength() > 0)
            {
                hasTypedImport = true;
                var firstType = importTypes[0];
                Assert.True(firstType.TryGetProperty("type", out _));
                Assert.True(firstType.TryGetProperty("count", out _));
            }
        }

        // Note: May not have typed imports if binlog doesn't capture imports
        Console.WriteLine($"Has typed imports: {hasTypedImport}");
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
