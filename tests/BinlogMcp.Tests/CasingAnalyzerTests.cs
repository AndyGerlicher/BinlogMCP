using BinlogMcp.CasingAnalyzer;
using Xunit;

namespace BinlogMcp.Tests;

/// <summary>
/// Tests for the CasingAnalyzer.
/// Uses a fixture with intentional casing errors (e.g., ..\librarya\ instead of ..\LibraryA\)
/// to verify the fixer correctly updates path segments.
/// </summary>
public class CasingAnalyzerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fixtureDir;

    public CasingAnalyzerTests()
    {
        // Find the fixture directory by walking up from the test assembly location
        var currentDir = AppContext.BaseDirectory;
        while (currentDir != null && !Directory.Exists(Path.Combine(currentDir, "test-data", "CasingFixture")))
        {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        _fixtureDir = currentDir != null
            ? Path.Combine(currentDir, "test-data", "CasingFixture")
            : throw new InvalidOperationException("Could not find test-data/CasingFixture directory");

        // Create temp directory for this test run
        _tempDir = Path.Combine(Path.GetTempPath(), "CasingAnalyzerTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        // Clean up temp directory
        try
        {
            if (Directory.Exists(_tempDir))
            {
                foreach (var file in Directory.GetFiles(_tempDir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public async Task FixDirectory_CorrectsCasingInFiles()
    {
        // Arrange: Copy fixture to temp directory
        CopyDirectory(_fixtureDir, _tempDir);

        // Read original file contents to verify they have the incorrect casing
        var appCsprojPath = Path.Combine(_tempDir, "App", "App.csproj");
        var libraryBCsprojPath = Path.Combine(_tempDir, "LibraryB", "LibraryB.csproj");

        var originalAppContent = await File.ReadAllTextAsync(appCsprojPath);
        var originalLibraryBContent = await File.ReadAllTextAsync(libraryBCsprojPath);

        // Verify the fixtures have incorrect casing
        Assert.Contains(@"..\librarya\", originalAppContent, StringComparison.Ordinal);
        Assert.Contains(@"..\libraryb\", originalAppContent, StringComparison.Ordinal);
        Assert.Contains(@"..\shared\", originalAppContent, StringComparison.Ordinal);
        Assert.Contains(@"..\librarya\", originalLibraryBContent, StringComparison.Ordinal);

        // Act: Create resolver with canonical paths from the temp directory
        var resolver = new CanonicalPathResolver(_tempDir);
        var fixer = new CasingFixer(resolver);
        await fixer.FixDirectoryAsync(_tempDir);

        // Assert: Files should have been modified
        Assert.True(fixer.FilesModified > 0, $"No files were modified");
        Assert.True(fixer.TotalReplacements > 0, "No replacements were made");

        // Assert: Verify the actual file contents are fixed
        var fixedAppContent = await File.ReadAllTextAsync(appCsprojPath);
        var fixedLibraryBContent = await File.ReadAllTextAsync(libraryBCsprojPath);

        // App.csproj should now have correct casing
        Assert.Contains(@"..\LibraryA\", fixedAppContent);
        Assert.Contains(@"..\LibraryB\", fixedAppContent);
        Assert.Contains(@"..\Shared\", fixedAppContent);
        Assert.DoesNotContain(@"..\librarya\", fixedAppContent, StringComparison.Ordinal);
        Assert.DoesNotContain(@"..\libraryb\", fixedAppContent, StringComparison.Ordinal);
        Assert.DoesNotContain(@"..\shared\", fixedAppContent, StringComparison.Ordinal);

        // LibraryB.csproj should now have correct casing for LibraryA
        Assert.Contains(@"..\LibraryA\", fixedLibraryBContent);
        Assert.DoesNotContain(@"..\librarya\", fixedLibraryBContent, StringComparison.Ordinal);

        // Verify the path structure is preserved (not expanded to absolute)
        Assert.DoesNotContain(_tempDir, fixedAppContent);
        Assert.DoesNotContain(_tempDir, fixedLibraryBContent);
    }

    [Fact]
    public void CanonicalPathResolver_LoadsFromFileSystem()
    {
        // Arrange: Copy fixture to temp directory
        CopyDirectory(_fixtureDir, _tempDir);

        // Act: Create resolver
        var resolver = new CanonicalPathResolver(_tempDir);

        // Assert: Should find canonical casing for directories
        Assert.Equal("LibraryA", resolver.GetCanonicalSegment("librarya"));
        Assert.Equal("LibraryB", resolver.GetCanonicalSegment("libraryb"));
        Assert.Equal("Shared", resolver.GetCanonicalSegment("shared"));
        Assert.Equal("App", resolver.GetCanonicalSegment("app"));
    }

    [Fact]
    public void CanonicalPathResolver_ReturnsNullForUnknownSegments()
    {
        // Arrange: Copy fixture to temp directory
        CopyDirectory(_fixtureDir, _tempDir);

        // Act: Create resolver
        var resolver = new CanonicalPathResolver(_tempDir);

        // Assert: Unknown segments should return null
        Assert.Null(resolver.GetCanonicalSegment("nonexistent"));
        Assert.Null(resolver.GetCanonicalSegment("foobar"));
    }

    [Fact]
    public async Task FixFile_DoesNotChangeXmlElementContent()
    {
        // Arrange: Create a test file with XML content that looks like it could be a path segment
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        var content = @"<Project>
  <PropertyGroup>
    <ModuleType>Theme</ModuleType>
    <OutputType>Library</OutputType>
  </PropertyGroup>
</Project>";
        await File.WriteAllTextAsync(testFile, content);

        // Create a directory called "theme" to potentially cause a false positive
        Directory.CreateDirectory(Path.Combine(_tempDir, "theme"));

        // Act: Fix the file
        var resolver = new CanonicalPathResolver(_tempDir);
        var fixer = new CasingFixer(resolver);
        var result = await fixer.FixFileAsync(testFile);

        // Assert: File should NOT be modified - "Theme" is XML content, not a path
        Assert.False(result.Modified, "XML element content should not be treated as path segments");

        var fixedContent = await File.ReadAllTextAsync(testFile);
        Assert.Contains("<ModuleType>Theme</ModuleType>", fixedContent);
        Assert.Contains("<OutputType>Library</OutputType>", fixedContent);
    }

    [Fact]
    public async Task FixFile_PreservesPathStructure()
    {
        // Arrange: Create a test file with paths
        var testFile = Path.Combine(_tempDir, "Test.csproj");
        var content = @"<Project>
  <Import Project=""..\shared\Shared.props"" />
  <ItemGroup>
    <ProjectReference Include=""..\librarya\LibraryA.csproj"" />
  </ItemGroup>
</Project>";
        await File.WriteAllTextAsync(testFile, content);

        // Create the directories so the resolver knows the canonical casing
        Directory.CreateDirectory(Path.Combine(_tempDir, "Shared"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "LibraryA"));

        // Act: Fix the file
        var resolver = new CanonicalPathResolver(_tempDir);
        var fixer = new CasingFixer(resolver);
        var result = await fixer.FixFileAsync(testFile);

        // Assert: File should be fixed
        Assert.True(result.Modified);

        var fixedContent = await File.ReadAllTextAsync(testFile);

        // Verify casing is fixed
        Assert.Contains(@"..\Shared\", fixedContent);
        Assert.Contains(@"..\LibraryA\", fixedContent);

        // Verify structure is preserved (relative paths, not absolute)
        Assert.DoesNotContain(_tempDir, fixedContent);
        Assert.Contains(@"..\", fixedContent); // Still has relative paths
    }

    #region Helper Methods

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(dir);
            if (dirName is "bin" or "obj")
                continue;

            var targetSubDir = Path.Combine(targetDir, dirName);
            CopyDirectory(dir, targetSubDir);
        }
    }

    #endregion
}
