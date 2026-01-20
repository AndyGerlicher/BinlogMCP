using Microsoft.Build.Logging.StructuredLogger;
using System.Collections.Concurrent;

namespace BinlogMcp.Tools;

/// <summary>
/// Thread-safe cache for parsed binlog Build objects.
/// Caches by file path + last modified time to detect changes.
/// Uses LRU eviction when capacity is reached.
/// </summary>
public static class BinlogCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly object _evictionLock = new();

    /// <summary>
    /// Maximum number of binlogs to cache. Default is 10.
    /// Can be configured via BINLOG_CACHE_SIZE environment variable.
    /// </summary>
    public static int MaxCacheSize { get; set; } = GetConfiguredCacheSize();

    /// <summary>
    /// Whether caching is enabled. Default is true.
    /// Can be disabled via BINLOG_CACHE_ENABLED=false environment variable.
    /// </summary>
    public static bool IsEnabled { get; set; } = GetCacheEnabled();

    private static int GetConfiguredCacheSize()
    {
        var envValue = Environment.GetEnvironmentVariable("BINLOG_CACHE_SIZE");
        if (int.TryParse(envValue, out var size) && size > 0)
            return size;
        return 10;
    }

    private static bool GetCacheEnabled()
    {
        var envValue = Environment.GetEnvironmentVariable("BINLOG_CACHE_ENABLED");
        if (string.Equals(envValue, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>
    /// Gets a Build object from cache or loads it from disk.
    /// </summary>
    public static Build GetOrLoad(string binlogPath)
    {
        if (!IsEnabled)
            return BinaryLog.ReadBuild(binlogPath);

        var normalizedPath = Path.GetFullPath(binlogPath);
        var fileInfo = new FileInfo(normalizedPath);

        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Binlog file not found: {binlogPath}");

        var cacheKey = normalizedPath.ToLowerInvariant();
        var fileLastModified = fileInfo.LastWriteTimeUtc;

        // Try to get from cache
        if (_cache.TryGetValue(cacheKey, out var entry))
        {
            // Check if file has been modified
            if (entry.LastModified == fileLastModified)
            {
                entry.LastAccessed = DateTime.UtcNow;
                return entry.Build;
            }

            // File changed, remove stale entry
            _cache.TryRemove(cacheKey, out _);
        }

        // Load from disk
        var build = BinaryLog.ReadBuild(normalizedPath);

        // Add to cache with eviction if needed
        var newEntry = new CacheEntry(build, fileLastModified);
        AddToCache(cacheKey, newEntry);

        return build;
    }

    private static void AddToCache(string key, CacheEntry entry)
    {
        // Check if we need to evict
        if (_cache.Count >= MaxCacheSize)
        {
            lock (_evictionLock)
            {
                // Double-check after acquiring lock
                if (_cache.Count >= MaxCacheSize)
                {
                    EvictLeastRecentlyUsed();
                }
            }
        }

        _cache[key] = entry;
    }

    private static void EvictLeastRecentlyUsed()
    {
        // Find the least recently used entry
        var lruKey = _cache
            .OrderBy(kvp => kvp.Value.LastAccessed)
            .Select(kvp => kvp.Key)
            .FirstOrDefault();

        if (lruKey != null)
        {
            _cache.TryRemove(lruKey, out _);
        }
    }

    /// <summary>
    /// Gets the current number of cached binlogs.
    /// </summary>
    public static int Count => _cache.Count;

    /// <summary>
    /// Clears all cached binlogs.
    /// </summary>
    public static void Clear()
    {
        _cache.Clear();
    }

    /// <summary>
    /// Removes a specific binlog from the cache.
    /// </summary>
    public static bool Remove(string binlogPath)
    {
        var normalizedPath = Path.GetFullPath(binlogPath).ToLowerInvariant();
        return _cache.TryRemove(normalizedPath, out _);
    }

    /// <summary>
    /// Gets cache statistics for monitoring.
    /// </summary>
    public static CacheStats GetStats()
    {
        var entries = _cache.ToArray();
        var totalMemoryEstimate = entries.Sum(e => EstimateMemoryUsage(e.Value.Build));

        return new CacheStats
        {
            CachedCount = entries.Length,
            MaxSize = MaxCacheSize,
            IsEnabled = IsEnabled,
            EstimatedMemoryMB = totalMemoryEstimate / 1024.0 / 1024.0,
            CachedFiles = entries.Select(e => new CachedFileInfo
            {
                Path = e.Key,
                LastModified = e.Value.LastModified,
                LastAccessed = e.Value.LastAccessed
            }).ToList()
        };
    }

    private static long EstimateMemoryUsage(Build build)
    {
        // Rough estimate based on typical binlog memory overhead
        // The parsed tree is typically 5-10x the file size
        // We estimate based on node count with a more conservative multiplier
        long nodeCount = 0;
        build.VisitAllChildren<BaseNode>(_ => nodeCount++);
        // ~50 bytes per node is more realistic for the tree structure itself
        // String content is interned/shared so less overhead than expected
        return nodeCount * 50;
    }

    private sealed class CacheEntry(Build build, DateTime lastModified)
    {
        public Build Build { get; } = build;
        public DateTime LastModified { get; } = lastModified;
        public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    }

    public sealed class CacheStats
    {
        public int CachedCount { get; init; }
        public int MaxSize { get; init; }
        public bool IsEnabled { get; init; }
        public double EstimatedMemoryMB { get; init; }
        public List<CachedFileInfo> CachedFiles { get; init; } = [];
    }

    public sealed class CachedFileInfo
    {
        public required string Path { get; init; }
        public DateTime LastModified { get; init; }
        public DateTime LastAccessed { get; init; }
    }
}
