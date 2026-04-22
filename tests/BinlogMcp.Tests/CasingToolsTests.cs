using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class CasingToolsTests : IDisposable
{
    private readonly string _tempDir;

    public CasingToolsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CasingToolsTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    #region GetCasingMismatches Tests

    [Fact]
    public void GetCasingMismatches_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetCasingMismatches(@"C:\nonexistent\file.binlog");
        var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetCasingMismatches_RealBinlog_ReturnsValidJson()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetCasingMismatches(binlogPath);
        var json = JsonDocument.Parse(result);

        // Should have the expected structure
        Assert.True(json.RootElement.TryGetProperty("totalMismatches", out _));
        Assert.True(json.RootElement.TryGetProperty("mismatches", out var mismatches));
        Assert.Equal(JsonValueKind.Array, mismatches.ValueKind);

        // Each mismatch should have required fields
        foreach (var m in mismatches.EnumerateArray())
        {
            Assert.True(m.TryGetProperty("observedPath", out _));
            Assert.True(m.TryGetProperty("canonicalPath", out _));
            Assert.True(m.TryGetProperty("mismatchSegments", out _));
            Assert.True(m.TryGetProperty("originKind", out _));
        }
    }

    [Fact]
    public void GetCasingMismatches_WithProjectFilter_ReturnsFiltered()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetCasingMismatches(binlogPath, projectFilter: "NonExistentProject");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("totalMismatches", out var count));
        Assert.Equal(0, count.GetInt32());
    }

    [Fact]
    public void GetCasingMismatches_WithLimit_RespectsLimit()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null) return;

        var result = BinlogTools.GetCasingMismatches(binlogPath, limit: 1);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("mismatches", out var mismatches));
        Assert.True(mismatches.GetArrayLength() <= 1);
    }

    #endregion

    #region FixCasingMismatch Tests

    [Fact]
    public void FixCasingMismatch_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.FixCasingMismatch(
            @"C:\nonexistent\file.csproj",
            oldValue: @"src\controllers",
            newValue: @"src\Controllers");
        var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void FixCasingMismatch_OutsideRepoRoot_ReturnsError()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        File.WriteAllText(testFile, "<Project />");

        var result = BinlogTools.FixCasingMismatch(
            testFile,
            oldValue: @"src\controllers",
            newValue: @"src\Controllers",
            repoRoot: @"C:\some\other\repo");
        var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("outside the repo root", error.GetString());
    }

    [Fact]
    public void FixCasingMismatch_IdenticalValues_ReturnsNoChange()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        File.WriteAllText(testFile, "<Project />");

        var result = BinlogTools.FixCasingMismatch(testFile, "same", "same");
        var json = JsonDocument.Parse(result);
        Assert.Equal("no_change", json.RootElement.GetProperty("result").GetString());
    }

    [Fact]
    public void FixCasingMismatch_NonCasingDifference_ReturnsError()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        File.WriteAllText(testFile, "<Project />");

        var result = BinlogTools.FixCasingMismatch(testFile, "completely", "different");
        var json = JsonDocument.Parse(result);
        Assert.True(json.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void FixCasingMismatch_DryRun_DoesNotModifyFile()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        var content = @"<Project>
  <PropertyGroup>
    <OutputPath>..\bin\debug</OutputPath>
  </PropertyGroup>
</Project>";
        File.WriteAllText(testFile, content);

        var result = BinlogTools.FixCasingMismatch(
            testFile,
            oldValue: @"..\bin\debug",
            newValue: @"..\bin\Debug",
            dryRun: true);

        var json = JsonDocument.Parse(result);
        Assert.Equal("dry_run", json.RootElement.GetProperty("result").GetString());
        Assert.True(json.RootElement.GetProperty("replacementCount").GetInt32() > 0);

        // File should NOT be modified
        var afterContent = File.ReadAllText(testFile);
        Assert.Equal(content, afterContent);
    }

    [Fact]
    public void FixCasingMismatch_Apply_ModifiesFile()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        var content = @"<Project>
  <PropertyGroup>
    <OutputPath>..\bin\debug</OutputPath>
  </PropertyGroup>
</Project>";
        File.WriteAllText(testFile, content);

        var result = BinlogTools.FixCasingMismatch(
            testFile,
            oldValue: @"..\bin\debug",
            newValue: @"..\bin\Debug",
            dryRun: false);

        var json = JsonDocument.Parse(result);
        Assert.Equal("fixed", json.RootElement.GetProperty("result").GetString());

        // File SHOULD be modified
        var afterContent = File.ReadAllText(testFile);
        Assert.Contains(@"..\bin\Debug", afterContent);
        Assert.DoesNotContain(@"..\bin\debug", afterContent);
    }

    [Fact]
    public void FixCasingMismatch_WithElementName_ScopesToElement()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        var content = @"<Project>
  <PropertyGroup>
    <OutputPath>..\bin\debug</OutputPath>
    <OtherPath>..\bin\debug</OtherPath>
  </PropertyGroup>
</Project>";
        File.WriteAllText(testFile, content);

        var result = BinlogTools.FixCasingMismatch(
            testFile,
            oldValue: @"..\bin\debug",
            newValue: @"..\bin\Debug",
            elementName: "OutputPath",
            dryRun: false);

        var json = JsonDocument.Parse(result);
        Assert.Equal("fixed", json.RootElement.GetProperty("result").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("replacementCount").GetInt32());

        // Only OutputPath should be fixed, not OtherPath
        var afterContent = File.ReadAllText(testFile);
        Assert.Contains(@"<OutputPath>..\bin\Debug</OutputPath>", afterContent);
        Assert.Contains(@"<OtherPath>..\bin\debug</OtherPath>", afterContent);
    }

    [Fact]
    public void FixCasingMismatch_FixesAttributes()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        var content = @"<Project>
  <ItemGroup>
    <ProjectReference Include=""..\librarya\LibraryA.csproj"" />
  </ItemGroup>
</Project>";
        File.WriteAllText(testFile, content);

        var result = BinlogTools.FixCasingMismatch(
            testFile,
            oldValue: @"..\librarya\",
            newValue: @"..\LibraryA\",
            dryRun: false);

        var json = JsonDocument.Parse(result);
        Assert.Equal("fixed", json.RootElement.GetProperty("result").GetString());

        var afterContent = File.ReadAllText(testFile);
        Assert.Contains(@"..\LibraryA\", afterContent);
    }

    [Fact]
    public void FixCasingMismatch_NotFound_ReturnsNotFound()
    {
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        File.WriteAllText(testFile, "<Project />");

        var result = BinlogTools.FixCasingMismatch(
            testFile,
            oldValue: @"nonexistent\path",
            newValue: @"Nonexistent\Path");

        var json = JsonDocument.Parse(result);
        Assert.Equal("not_found", json.RootElement.GetProperty("result").GetString());
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

    #endregion
}
