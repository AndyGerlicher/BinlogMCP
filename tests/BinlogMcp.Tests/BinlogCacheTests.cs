using BinlogMcp.Tools;
using System.Text.Json;
using Xunit;

namespace BinlogMcp.Tests;

public class BinlogCacheTests : IDisposable
{
    private static string? _binlogPath;

    private static string BinlogPath
    {
        get
        {
            if (_binlogPath == null)
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "test.binlog")))
                {
                    dir = dir.Parent;
                }
                _binlogPath = dir != null ? Path.Combine(dir.FullName, "test.binlog") : "";
            }
            return _binlogPath;
        }
    }

    public BinlogCacheTests()
    {
        // Clear cache before each test
        BinlogCache.Clear();
    }

    public void Dispose()
    {
        // Clear cache after each test
        BinlogCache.Clear();
    }

    [Fact]
    public void GetOrLoad_CachesMissingThenHit()
    {
        if (!File.Exists(BinlogPath)) return;

        Assert.Equal(0, BinlogCache.Count);

        // First load - should be a miss
        var build1 = BinlogCache.GetOrLoad(BinlogPath);
        Assert.NotNull(build1);
        Assert.Equal(1, BinlogCache.Count);

        // Second load - should be a hit (same object)
        var build2 = BinlogCache.GetOrLoad(BinlogPath);
        Assert.Same(build1, build2);
        Assert.Equal(1, BinlogCache.Count);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        if (!File.Exists(BinlogPath)) return;

        BinlogCache.GetOrLoad(BinlogPath);
        Assert.Equal(1, BinlogCache.Count);

        BinlogCache.Clear();
        Assert.Equal(0, BinlogCache.Count);
    }

    [Fact]
    public void Remove_RemovesSpecificEntry()
    {
        if (!File.Exists(BinlogPath)) return;

        BinlogCache.GetOrLoad(BinlogPath);
        Assert.Equal(1, BinlogCache.Count);

        var removed = BinlogCache.Remove(BinlogPath);
        Assert.True(removed);
        Assert.Equal(0, BinlogCache.Count);
    }

    [Fact]
    public void GetStats_ReturnsCorrectStats()
    {
        if (!File.Exists(BinlogPath)) return;

        var statsBefore = BinlogCache.GetStats();
        Assert.Equal(0, statsBefore.CachedCount);
        Assert.True(statsBefore.IsEnabled);

        BinlogCache.GetOrLoad(BinlogPath);

        var statsAfter = BinlogCache.GetStats();
        Assert.Equal(1, statsAfter.CachedCount);
        Assert.Single(statsAfter.CachedFiles);
        Assert.True(statsAfter.EstimatedMemoryMB > 0);
    }

    [Fact]
    public void GetCacheStats_Tool_ReturnsJson()
    {
        if (!File.Exists(BinlogPath)) return;

        // Load a binlog first
        BinlogCache.GetOrLoad(BinlogPath);

        var result = BinlogTools.GetCacheStats();
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("cachedCount").GetInt32());
        Assert.Equal(10, json.RootElement.GetProperty("maxSize").GetInt32());
    }

    [Fact]
    public void GetCacheStats_ClearCache_ClearsAndReturnsStats()
    {
        if (!File.Exists(BinlogPath)) return;

        // Load a binlog first
        BinlogCache.GetOrLoad(BinlogPath);
        Assert.Equal(1, BinlogCache.Count);

        var result = BinlogTools.GetCacheStats(clearCache: true);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.GetProperty("cacheCleared").GetBoolean());
        Assert.Equal(0, BinlogCache.Count);
    }

    [Fact]
    public void Cache_NormalizesPath()
    {
        if (!File.Exists(BinlogPath)) return;

        // Load with original path
        BinlogCache.GetOrLoad(BinlogPath);
        Assert.Equal(1, BinlogCache.Count);

        // Load with different casing/slashes - should hit same cache entry
        var altPath = BinlogPath.Replace('\\', '/');
        BinlogCache.GetOrLoad(altPath);
        Assert.Equal(1, BinlogCache.Count); // Still only 1 entry
    }
}
