using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace BinlogMcp.Tools;

/// <summary>
/// Casing analysis tools - detect and fix path casing mismatches using binlog data.
/// </summary>
public static partial class BinlogTools
{
    [McpServerTool, Description("Scans a binlog for path casing mismatches - finds paths in properties, items, and imports where the casing differs from the actual filesystem. Returns mismatches with their definition site (source file, property/item name, context) so an agent can trace root causes and apply fixes.")]
    public static string GetCasingMismatches(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Root directory of the repository for resolving paths (optional - inferred from binlog if not specified)")] string? repoRoot = null,
        [Description("Filter to specific project name (optional)")] string? projectFilter = null,
        [Description("Maximum number of mismatches to return (default: 100)")] int limit = 100,
        [Description("Include paths outside the repo root (SDK, NuGet, etc.) - default: false")] bool includeExternal = false)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            // Infer repo root from the binlog if not provided
            var effectiveRoot = repoRoot;
            if (string.IsNullOrEmpty(effectiveRoot))
            {
                effectiveRoot = InferRepoRoot(build);
            }

            if (!string.IsNullOrEmpty(effectiveRoot))
            {
                effectiveRoot = Path.GetFullPath(effectiveRoot);
            }

            var mismatches = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Collect nodes from the binlog
            var properties = new List<Property>();
            var addItems = new List<AddItem>();
            var imports = new List<Import>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Property p:
                        properties.Add(p);
                        break;
                    case AddItem a:
                        addItems.Add(a);
                        break;
                    case Import i:
                        imports.Add(i);
                        break;
                }
            });

            // 1. Check properties with path-like values
            foreach (var prop in properties)
            {
                if (mismatches.Count >= limit) break;

                var projectName = GetProjectName(prop);
                if (projectName.FailsFilter(projectFilter)) continue;

                if (string.IsNullOrEmpty(prop.Value)) continue;
                if (!LooksLikePath(prop.Value)) continue;
                // Skip MSBuild expressions that haven't been fully evaluated
                if (ContainsMSBuildExpression(prop.Value)) continue;

                var mismatch = CheckPathCasing(prop.Value, effectiveRoot, includeExternal, seen);
                if (mismatch == null) continue;

                var source = DeterminePropertySource(prop);
                mismatches.Add(new
                {
                    observedPath = prop.Value,
                    canonicalPath = mismatch.Value.canonicalPath,
                    mismatchSegments = mismatch.Value.mismatchSegments,
                    originKind = "Property",
                    propertyOrItemName = prop.Name,
                    sourceFile = source.sourceFile,
                    sourceContext = source.context,
                    projectFile = GetProjectFile(prop),
                    project = projectName,
                    isRepoLocal = IsUnderRoot(prop.Value, effectiveRoot),
                });
            }

            // 2. Check item specs
            foreach (var addItem in addItems)
            {
                if (mismatches.Count >= limit) break;

                var projectName = GetProjectName(addItem);
                if (projectName.FailsFilter(projectFilter)) continue;

                foreach (var item in addItem.Children.OfType<Item>())
                {
                    if (mismatches.Count >= limit) break;
                    if (string.IsNullOrEmpty(item.Text)) continue;
                    if (!LooksLikePath(item.Text)) continue;
                    if (ContainsMSBuildExpression(item.Text)) continue;

                    var mismatch = CheckPathCasing(item.Text, effectiveRoot, includeExternal, seen);
                    if (mismatch == null) continue;

                    var source = DetermineItemSource(addItem);
                    mismatches.Add(new
                    {
                        observedPath = item.Text,
                        canonicalPath = mismatch.Value.canonicalPath,
                        mismatchSegments = mismatch.Value.mismatchSegments,
                        originKind = "Item",
                        propertyOrItemName = addItem.Name,
                        sourceFile = source.sourceFile,
                        sourceContext = source.context,
                        projectFile = GetProjectFile(addItem),
                        project = projectName,
                        isRepoLocal = IsUnderRoot(item.Text, effectiveRoot),
                    });
                }
            }

            // 3. Check import paths
            foreach (var import in imports)
            {
                if (mismatches.Count >= limit) break;

                var importedPath = import.ImportedProjectFilePath ?? import.ProjectFilePath;
                if (string.IsNullOrEmpty(importedPath)) continue;
                if (ContainsMSBuildExpression(importedPath)) continue;

                var mismatch = CheckPathCasing(importedPath, effectiveRoot, includeExternal, seen);
                if (mismatch == null) continue;

                var projectName = GetProjectNameForImport(import);
                if (projectName.FailsFilter(projectFilter)) continue;

                mismatches.Add(new
                {
                    observedPath = importedPath,
                    canonicalPath = mismatch.Value.canonicalPath,
                    mismatchSegments = mismatch.Value.mismatchSegments,
                    originKind = "Import",
                    propertyOrItemName = (string?)null,
                    sourceFile = import.ProjectFilePath,
                    sourceContext = $"Import of {Path.GetFileName(importedPath)}",
                    projectFile = import.ProjectFilePath,
                    project = projectName,
                    isRepoLocal = IsUnderRoot(importedPath, effectiveRoot),
                });
            }

            // Summary
            var repoLocalCount = mismatches.Count(m =>
            {
                var json = JsonSerializer.Serialize(m);
                return json.Contains("\"isRepoLocal\": true") || json.Contains("\"isRepoLocal\":true");
            });

            return new
            {
                binlogPath,
                repoRoot = effectiveRoot,
                totalMismatches = mismatches.Count,
                truncated = mismatches.Count >= limit,
                mismatches
            };
        });
    }

    [McpServerTool, Description("Fixes a specific path casing mismatch in an MSBuild source file. Uses XML-aware editing to surgically replace the incorrect casing in the identified property or item element. Always verify the mismatch first with GetCasingMismatches.")]
    public static string FixCasingMismatch(
        [Description("Path to the MSBuild source file (.csproj, .props, .targets, etc.)")] string sourceFile,
        [Description("The old (incorrect) path value to find")] string oldValue,
        [Description("The new (correct) path value to replace with")] string newValue,
        [Description("Property or item name to scope the fix (optional - if provided, only fixes within that element)")] string? elementName = null,
        [Description("Dry run - report what would change without modifying the file (default: true)")] bool dryRun = true)
    {
        var validationError = ValidateFileExists(sourceFile);
        if (validationError != null) return validationError;

        try
        {
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                return JsonSerializer.Serialize(new { result = "no_change", message = "Old and new values are identical" }, JsonOptions);

            if (!string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { error = "Old and new values differ by more than casing. This tool only fixes casing differences." }, JsonOptions);

            // Read with encoding detection
            var encoding = DetectEncoding(sourceFile);
            var content = File.ReadAllText(sourceFile, encoding);

            XDocument doc;
            try
            {
                doc = XDocument.Parse(content, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = $"Failed to parse XML: {ex.Message}" }, JsonOptions);
            }

            var replacements = new List<object>();
            var modified = false;

            // Walk all elements looking for matching values
            foreach (var element in doc.Descendants())
            {
                // If elementName is specified, only look at matching elements
                if (!string.IsNullOrEmpty(elementName) &&
                    !element.Name.LocalName.Equals(elementName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check element text content
                if (element.Value != null && !element.HasElements)
                {
                    var text = element.Value;
                    if (ContainsPathCaseMismatch(text, oldValue, out var fixedText, newValue))
                    {
                        replacements.Add(new
                        {
                            element = element.Name.LocalName,
                            attribute = (string?)null,
                            oldText = text,
                            newText = fixedText,
                            line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : (int?)null
                        });

                        if (!dryRun)
                        {
                            element.Value = fixedText;
                            modified = true;
                        }
                    }
                }

                // Check attributes
                foreach (var attr in element.Attributes())
                {
                    if (ContainsPathCaseMismatch(attr.Value, oldValue, out var fixedAttr, newValue))
                    {
                        replacements.Add(new
                        {
                            element = element.Name.LocalName,
                            attribute = attr.Name.LocalName,
                            oldText = attr.Value,
                            newText = fixedAttr,
                            line = ((IXmlLineInfo)element).HasLineInfo() ? ((IXmlLineInfo)element).LineNumber : (int?)null
                        });

                        if (!dryRun)
                        {
                            attr.Value = fixedAttr;
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                // Write back preserving encoding
                using var writer = new StreamWriter(sourceFile, false, encoding);
                doc.Save(writer, SaveOptions.DisableFormatting);
            }

            return JsonSerializer.Serialize(new
            {
                result = replacements.Count == 0 ? "not_found" : (dryRun ? "dry_run" : "fixed"),
                sourceFile,
                dryRun,
                replacementCount = replacements.Count,
                replacements,
                message = replacements.Count == 0
                    ? $"No occurrences of '{oldValue}' found in {sourceFile}"
                    : dryRun
                        ? $"Would fix {replacements.Count} occurrence(s). Set dryRun=false to apply."
                        : $"Fixed {replacements.Count} occurrence(s) in {sourceFile}"
            }, JsonOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Failed to fix casing: {ex.Message}" }, JsonOptions);
        }
    }

    #region Casing Analysis Helpers

    /// <summary>
    /// Checks whether a path has casing that differs from disk. Returns null if no mismatch.
    /// </summary>
    private static (string canonicalPath, List<object> mismatchSegments)? CheckPathCasing(
        string observedPath, string? repoRoot, bool includeExternal, HashSet<string> seen)
    {
        // Normalize to absolute path
        string absolutePath;
        try
        {
            absolutePath = Path.GetFullPath(observedPath);
        }
        catch
        {
            return null; // Invalid path
        }

        // Dedup
        if (!seen.Add(absolutePath)) return null;

        // Filter to repo-local if requested
        if (!includeExternal && !IsUnderRoot(absolutePath, repoRoot))
            return null;

        // The path must exist on disk to verify casing
        if (!File.Exists(absolutePath) && !Directory.Exists(absolutePath))
            return null;

        // Resolve canonical casing from disk segment by segment
        var canonical = ResolveCanonicalPath(absolutePath);
        if (canonical == null) return null;

        // Compare segment by segment
        var observedSegments = absolutePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var canonicalSegments = canonical.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        if (observedSegments.Length != canonicalSegments.Length) return null;

        var mismatches = new List<object>();
        for (int i = 0; i < observedSegments.Length; i++)
        {
            if (!string.Equals(observedSegments[i], canonicalSegments[i], StringComparison.Ordinal) &&
                string.Equals(observedSegments[i], canonicalSegments[i], StringComparison.OrdinalIgnoreCase))
            {
                mismatches.Add(new
                {
                    index = i,
                    observed = observedSegments[i],
                    canonical = canonicalSegments[i]
                });
            }
        }

        if (mismatches.Count == 0) return null;

        return (canonical, mismatches);
    }

    /// <summary>
    /// Resolves the canonical (on-disk) casing for a path by walking the filesystem directory by directory.
    /// </summary>
    internal static string? ResolveCanonicalPath(string path)
    {
        try
        {
            // Normalize
            path = Path.GetFullPath(path);

            // Get the root (e.g., "C:\")
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return null;

            // Start from the root and resolve each segment
            var segments = path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            var currentPath = root;

            foreach (var segment in segments)
            {
                var dirInfo = new DirectoryInfo(currentPath);
                if (!dirInfo.Exists) return null;

                // Look for matching entry (case-insensitive) and get its actual name
                FileSystemInfo? match = null;

                try
                {
                    // Check directories first
                    foreach (var dir in dirInfo.GetDirectories())
                    {
                        if (string.Equals(dir.Name, segment, StringComparison.OrdinalIgnoreCase))
                        {
                            match = dir;
                            break;
                        }
                    }

                    // Then check files
                    if (match == null)
                    {
                        foreach (var file in dirInfo.GetFiles())
                        {
                            if (string.Equals(file.Name, segment, StringComparison.OrdinalIgnoreCase))
                            {
                                match = file;
                                break;
                            }
                        }
                    }
                }
                catch
                {
                    return null; // Permission denied, etc.
                }

                if (match == null) return null; // Segment doesn't exist

                currentPath = Path.Combine(currentPath, match.Name);
            }

            return currentPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Determines if a string looks like a filesystem path.
    /// </summary>
    private static bool LooksLikePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length < 3) return false;

        // Must contain path separators
        if (!value.Contains('\\') && !value.Contains('/')) return false;

        // Skip URLs
        if (value.Contains("://")) return false;

        // Skip very long values (unlikely to be a single path)
        if (value.Length > 500) return false;

        // Skip values with newlines (multi-line property values)
        if (value.Contains('\n') || value.Contains('\r')) return false;

        // Skip values with semicolons (item lists)
        if (value.Contains(';')) return false;

        return true;
    }

    /// <summary>
    /// Checks if a value contains unexpanded MSBuild expressions.
    /// </summary>
    private static bool ContainsMSBuildExpression(string value)
    {
        return value.Contains("$(") || value.Contains("@(") || value.Contains("%(");
    }

    /// <summary>
    /// Checks if a path is under a given root directory.
    /// </summary>
    private static bool IsUnderRoot(string path, string? root)
    {
        if (string.IsNullOrEmpty(root)) return true; // If no root, assume yes
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Infers the repo root from the binlog's project files.
    /// Walks up from the main project file looking for common repo root indicators.
    /// </summary>
    private static string? InferRepoRoot(Build build)
    {
        string? mainProjectFile = null;

        build.VisitAllChildren<BaseNode>(node =>
        {
            if (mainProjectFile != null) return;
            if (node is Project p && !string.IsNullOrEmpty(p.ProjectFile))
            {
                mainProjectFile = p.ProjectFile;
            }
        });

        if (string.IsNullOrEmpty(mainProjectFile)) return null;

        // Walk up looking for .git, .sln, Directory.Build.props, etc.
        var dir = Path.GetDirectoryName(mainProjectFile);
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) ||
                Directory.GetFiles(dir, "*.sln").Length > 0 ||
                Directory.GetFiles(dir, "*.slnx").Length > 0 ||
                File.Exists(Path.Combine(dir, "Directory.Build.props")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }

        // Fall back to the project's directory
        return Path.GetDirectoryName(mainProjectFile);
    }

    /// <summary>
    /// Gets the source context for an AddItem (which target/project defined it).
    /// </summary>
    private static (string? sourceFile, string? context) DetermineItemSource(AddItem addItem)
    {
        var current = (BaseNode?)addItem.Parent;
        string? sourceFile = null;
        string? context = null;

        while (current != null)
        {
            switch (current)
            {
                case Target target:
                    sourceFile ??= target.SourceFilePath;
                    context = $"Target: {target.Name}, Item: {addItem.Name}";
                    break;
                case Project project:
                    sourceFile ??= project.ProjectFile;
                    context ??= $"Project: {project.Name}, Item: {addItem.Name}";
                    break;
                case ProjectEvaluation eval:
                    sourceFile ??= eval.ProjectFile;
                    context ??= $"Evaluation, Item: {addItem.Name}";
                    break;
            }
            current = current.Parent;
        }

        return (sourceFile, context ?? $"Item: {addItem.Name}");
    }

    /// <summary>
    /// Gets the project name for an Import node.
    /// </summary>
    private static string? GetProjectNameForImport(Import import)
    {
        var current = (BaseNode?)import.Parent;
        while (current != null)
        {
            if (current is Project p) return p.Name;
            if (current is ProjectEvaluation pe) return Path.GetFileNameWithoutExtension(pe.ProjectFile);
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Checks if text contains a path that should be case-corrected.
    /// Returns the fixed text via out parameter.
    /// </summary>
    private static bool ContainsPathCaseMismatch(string text, string oldValue, out string fixedText, string newValue)
    {
        fixedText = text;

        // Find the oldValue in the text (case-insensitive match, case-sensitive check)
        var idx = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;

        // Verify it's actually a case mismatch (not already correct)
        var found = text.Substring(idx, oldValue.Length);
        if (string.Equals(found, newValue, StringComparison.Ordinal))
            return false; // Already correct

        if (!string.Equals(found, oldValue, StringComparison.OrdinalIgnoreCase))
            return false; // Doesn't match

        fixedText = text[..idx] + newValue + text[(idx + oldValue.Length)..];
        return true;
    }

    /// <summary>
    /// Detects the encoding of a file, preserving BOM.
    /// </summary>
    private static System.Text.Encoding DetectEncoding(string filePath)
    {
        using var reader = new StreamReader(filePath, detectEncodingFromByteOrderMarks: true);
        reader.Read(); // Trigger encoding detection
        return reader.CurrentEncoding;
    }

    #endregion
}
