using System.Collections.Concurrent;
using System.Diagnostics;

namespace BinlogMcp.CasingAnalyzer;

/// <summary>
/// Resolves canonical (correct) casing for paths.
/// Uses git ls-files first (fast, single call), falls back to disk.
/// Results are cached.
/// </summary>
public class CanonicalPathResolver
{
    private readonly string _repoRoot;
    private readonly ConcurrentDictionary<string, string> _segmentCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public CanonicalPathResolver(string repoRoot)
    {
        _repoRoot = Path.GetFullPath(repoRoot);
    }

    /// <summary>
    /// Gets the canonical casing for a path segment.
    /// Returns null if the segment is not known.
    /// </summary>
    public string? GetCanonicalSegment(string segment)
    {
        EnsureInitialized();
        return _segmentCache.TryGetValue(segment, out var canonical) ? canonical : null;
    }

    /// <summary>
    /// Builds the segment cache from filesystem (primary) and git (for file discovery).
    /// Disk casing is the source of truth - git may have wrong casing from commits.
    /// </summary>
    private void EnsureInitialized()
    {
        if (_initialized) return;

        // Always walk the filesystem for canonical casing - this is the source of truth
        // Git may have wrong casing if files were committed with incorrect casing
        BuildSegmentCacheFromFileSystem();

        _initialized = true;
    }

    private List<string>? TryGetGitFiles()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "ls-files",
                WorkingDirectory = _repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var files = new List<string>();
            string? line;
            while ((line = process.StandardOutput.ReadLine()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    // Git outputs paths relative to repo root with forward slashes
                    var fullPath = Path.GetFullPath(Path.Combine(_repoRoot, line.Replace('/', '\\')));
                    files.Add(fullPath);
                }
            }

            process.WaitForExit();
            return process.ExitCode == 0 ? files : null;
        }
        catch
        {
            return null;
        }
    }

    private void BuildSegmentCacheFromPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                // Skip drive letters
                if (segment.Length == 2 && segment[1] == ':')
                    continue;

                // Store the canonical casing (first one wins, which is fine since git is consistent)
                _segmentCache.TryAdd(segment, segment);
            }
        }
    }

    private void BuildSegmentCacheFromFileSystem()
    {
        // Walk the filesystem to get canonical directory/file names
        try
        {
            WalkDirectory(_repoRoot);
        }
        catch
        {
            // Ignore errors during filesystem walk
        }
    }

    private void WalkDirectory(string dir)
    {
        try
        {
            // Get actual directory name with correct casing
            var dirInfo = new DirectoryInfo(dir);
            if (dirInfo.Exists)
            {
                _segmentCache.TryAdd(dirInfo.Name, dirInfo.Name);

                // Get files with correct casing
                foreach (var file in dirInfo.GetFiles())
                {
                    _segmentCache.TryAdd(file.Name, file.Name);
                }

                // Recurse into subdirectories
                foreach (var subDir in dirInfo.GetDirectories())
                {
                    // Skip common directories that aren't relevant
                    if (subDir.Name is ".git" or "node_modules" or "bin" or "obj" or ".vs")
                        continue;

                    _segmentCache.TryAdd(subDir.Name, subDir.Name);
                    WalkDirectory(subDir.FullName);
                }
            }
        }
        catch
        {
            // Ignore errors (permission denied, etc.)
        }
    }

    /// <summary>
    /// Gets the canonical casing by checking the filesystem directly.
    /// Use this as a fallback for paths not found in the cache.
    /// </summary>
    public string? GetCanonicalSegmentFromDisk(string segment, string containingDir)
    {
        try
        {
            var dirInfo = new DirectoryInfo(containingDir);
            if (!dirInfo.Exists) return null;

            // Check files
            foreach (var file in dirInfo.GetFiles())
            {
                if (string.Equals(file.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    _segmentCache.TryAdd(file.Name, file.Name);
                    return file.Name;
                }
            }

            // Check directories
            foreach (var subDir in dirInfo.GetDirectories())
            {
                if (string.Equals(subDir.Name, segment, StringComparison.OrdinalIgnoreCase))
                {
                    _segmentCache.TryAdd(subDir.Name, subDir.Name);
                    return subDir.Name;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return null;
    }
}
