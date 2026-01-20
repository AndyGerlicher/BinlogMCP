using System.Diagnostics;
using BinlogMcp.CasingAnalyzer;

// Parse command line arguments
if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var repoRoot = args[0];

if (!Directory.Exists(repoRoot))
{
    Console.Error.WriteLine($"Error: Directory not found: {repoRoot}");
    return 1;
}

repoRoot = Path.GetFullPath(repoRoot);

// Progress reporting
var progress = new Progress<string>(Console.WriteLine);

Console.WriteLine($"Fixing casing in: {repoRoot}");
Console.WriteLine();

var stopwatch = Stopwatch.StartNew();

// Build canonical path resolver (uses git ls-files, falls back to disk)
Console.WriteLine("Loading canonical paths...");
var resolver = new CanonicalPathResolver(repoRoot);

// Fix source files
var fixer = new CasingFixer(resolver);
await fixer.FixDirectoryAsync(repoRoot, progress, CancellationToken.None);

stopwatch.Stop();

Console.WriteLine();
Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds:F2}s");
Console.WriteLine($"  Files modified: {fixer.FilesModified}");
Console.WriteLine($"  Total changes: {fixer.TotalReplacements}");

// Report errors if any
var errors = fixer.Results.Where(r => r.Error != null).ToList();
if (errors.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Errors:");
    foreach (var error in errors)
    {
        Console.WriteLine($"  {error.FilePath}: {error.Error}");
    }
}

return fixer.FilesModified > 0 ? 0 : 0; // Success either way

static void PrintUsage()
{
    Console.WriteLine("BinlogMcp.CasingAnalyzer - Fix path casing in MSBuild files");
    Console.WriteLine();
    Console.WriteLine("Usage: BinlogMcp.CasingAnalyzer <repo-root>");
    Console.WriteLine();
    Console.WriteLine("Arguments:");
    Console.WriteLine("  <repo-root>    Root directory of the repository");
    Console.WriteLine();
    Console.WriteLine("The tool will:");
    Console.WriteLine("  1. Get canonical file casing from git (or disk)");
    Console.WriteLine("  2. Scan all .props, .targets, *proj files");
    Console.WriteLine("  3. Fix any path segments with incorrect casing");
}
