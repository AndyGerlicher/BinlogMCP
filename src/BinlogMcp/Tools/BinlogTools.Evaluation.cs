using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace BinlogMcp.Tools;

/// <summary>
/// Project evaluation tools - shows the "flattened" view of a project after all imports and property evaluations.
/// </summary>
public static partial class BinlogTools
{
    [McpServerTool, Description("Shows the evaluated (flattened) view of a project - final property values, item groups, and import chain after all MSBuild evaluation is complete. This is useful for understanding what a project looks like after Directory.Build.props, SDK imports, and all property/item manipulations.")]
    public static string GetEvaluatedProject(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown, csv")] string format = "json",
        [Description("Filter to specific project name or file path (optional - if not specified, shows first/main project)")] string? projectFilter = null,
        [Description("Include all properties (default: false shows only important properties)")] bool includeAllProperties = false,
        [Description("Include item metadata details (default: false)")] bool includeItemMetadata = false,
        [Description("Maximum items per item type to return (default: 20)")] int maxItemsPerType = 20)
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Evaluated Project", build =>
        {
            // Collect all evaluation data
            var properties = new List<Property>();
            var addItems = new List<AddItem>();
            var imports = new List<Import>();
            var evaluations = new List<ProjectEvaluation>();
            var projects = new List<Project>();

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
                    case ProjectEvaluation pe:
                        evaluations.Add(pe);
                        break;
                    case Project proj:
                        projects.Add(proj);
                        break;
                }
            });

            // Determine which project to analyze
            var targetProject = DetermineTargetProject(evaluations, projects, projectFilter);
            if (targetProject == null)
            {
                return new
                {
                    error = projectFilter != null
                        ? $"Project matching '{projectFilter}' not found"
                        : "No projects found in binlog",
                    availableProjects = evaluations.Select(e => e.Name ?? Path.GetFileNameWithoutExtension(e.ProjectFile ?? "")).Distinct().Take(20).ToList()
                };
            }

            var projectName = targetProject.Value.name;
            var projectFile = targetProject.Value.file;

            // 1. Collect final property values for this project
            var projectProperties = properties
                .Where(p => MatchesProject(p, projectName, projectFile))
                .ToList();

            var finalProperties = GetFinalPropertyValues(projectProperties);

            // 2. Collect items for this project
            var projectItems = addItems
                .Where(a => MatchesProject(a, projectName, projectFile))
                .ToList();

            var itemGroups = GetItemGroups(projectItems, includeItemMetadata, maxItemsPerType);

            // 3. Collect import chain for this project
            var projectImports = GetProjectImports(imports, evaluations, projectName, projectFile);

            // 4. Detect property conflicts (properties set multiple times with different values)
            var propertyConflicts = DetectPropertyConflicts(projectProperties);

            // 5. Build project metadata
            var metadata = BuildProjectMetadata(finalProperties, projectName, projectFile);

            // 6. Filter properties based on includeAllProperties flag
            var outputProperties = includeAllProperties
                ? finalProperties.OrderBy(p => p.name).ToList()
                : GetImportantProperties(finalProperties);

            // 7. Categorize properties
            var categorizedProperties = CategorizeProperties(outputProperties);

            return new
            {
                file = binlogPath,
                project = new
                {
                    name = projectName,
                    projectFile,
                    metadata
                },
                summary = new
                {
                    totalProperties = finalProperties.Count,
                    shownProperties = outputProperties.Count,
                    totalItemTypes = itemGroups.Count,
                    totalItems = itemGroups.Sum(g => g.TotalCount),
                    totalImports = projectImports.Count,
                    propertyConflicts = propertyConflicts.Count
                },
                properties = categorizedProperties,
                itemGroups = itemGroups.Select(g => new
                {
                    itemType = g.ItemType,
                    totalCount = g.TotalCount,
                    shownCount = g.ShownCount,
                    items = g.Items
                }).ToList(),
                imports = projectImports.Select(i => new
                {
                    file = Path.GetFileName(i.file),
                    fullPath = TruncateValue(i.file, 120),
                    type = i.type,
                    order = i.order
                }).ToList(),
                conflicts = propertyConflicts.Count > 0 ? propertyConflicts : null
            };
        });
    }

    private static (string name, string? file)? DetermineTargetProject(
        List<ProjectEvaluation> evaluations,
        List<Project> projects,
        string? projectFilter)
    {
        // If filter specified, find matching project
        if (!string.IsNullOrEmpty(projectFilter))
        {
            // Try evaluations first (contains evaluation-time data)
            var matchingEval = evaluations.FirstOrDefault(e =>
                (e.Name?.Contains(projectFilter, StringComparison.OrdinalIgnoreCase) == true) ||
                (e.ProjectFile?.Contains(projectFilter, StringComparison.OrdinalIgnoreCase) == true));

            if (matchingEval != null)
            {
                return (matchingEval.Name ?? Path.GetFileNameWithoutExtension(matchingEval.ProjectFile ?? "Unknown"),
                        matchingEval.ProjectFile);
            }

            // Try projects
            var matchingProject = projects.FirstOrDefault(p =>
                (p.Name?.Contains(projectFilter, StringComparison.OrdinalIgnoreCase) == true) ||
                (p.ProjectFile?.Contains(projectFilter, StringComparison.OrdinalIgnoreCase) == true));

            if (matchingProject != null)
            {
                return (matchingProject.Name ?? Path.GetFileNameWithoutExtension(matchingProject.ProjectFile ?? "Unknown"),
                        matchingProject.ProjectFile);
            }

            return null;
        }

        // No filter - return first/main project
        // Prefer evaluations as they have evaluation-time data
        var firstEval = evaluations.FirstOrDefault();
        if (firstEval != null)
        {
            return (firstEval.Name ?? Path.GetFileNameWithoutExtension(firstEval.ProjectFile ?? "Unknown"),
                    firstEval.ProjectFile);
        }

        var firstProject = projects.FirstOrDefault();
        if (firstProject != null)
        {
            return (firstProject.Name ?? Path.GetFileNameWithoutExtension(firstProject.ProjectFile ?? "Unknown"),
                    firstProject.ProjectFile);
        }

        return null;
    }

    private static bool MatchesProject(BaseNode node, string projectName, string? projectFile)
    {
        var nodeProjName = GetProjectName(node);
        var nodeProjFile = GetProjectFile(node);

        // Match by name or file path
        if (nodeProjName?.Equals(projectName, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        if (projectFile != null && nodeProjFile?.Equals(projectFile, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        // Also match if project name is contained (for multi-targeting like "MyProject (net8.0)")
        if (nodeProjName?.Contains(projectName, StringComparison.OrdinalIgnoreCase) == true)
            return true;

        return false;
    }

    private static List<(string name, string? value, string? source)> GetFinalPropertyValues(List<Property> properties)
    {
        // Group by property name and take the last value (final evaluated value)
        var grouped = properties
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var last = g.Last();
                var source = GetPropertyOriginInfo(last);
                return (name: g.Key, value: (string?)last.Value, source: source.context);
            })
            .ToList();

        return grouped;
    }

    private static List<ItemGroupInfo> GetItemGroups(List<AddItem> addItems, bool includeMetadata, int maxItemsPerType)
    {
        var grouped = addItems
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var allItems = g.SelectMany(a => a.Children.OfType<Item>()).ToList();
                var distinctItems = allItems
                    .Select(i => i.Text)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                object items;
                if (includeMetadata)
                {
                    items = allItems
                        .DistinctBy(i => i.Text, StringComparer.OrdinalIgnoreCase)
                        .Take(maxItemsPerType)
                        .Select(i => new
                        {
                            include = i.Text,
                            metadata = GetItemMetadataDict(i)
                        })
                        .ToList();
                }
                else
                {
                    items = distinctItems.Take(maxItemsPerType).ToList();
                }

                return new ItemGroupInfo
                {
                    ItemType = g.Key,
                    TotalCount = distinctItems.Count,
                    ShownCount = Math.Min(distinctItems.Count, maxItemsPerType),
                    Items = items
                };
            })
            .OrderByDescending(x => x.TotalCount)
            .ToList();

        return grouped;
    }

    private class ItemGroupInfo
    {
        public required string ItemType { get; set; }
        public int TotalCount { get; set; }
        public int ShownCount { get; set; }
        public required object Items { get; set; }
    }

    private static List<(string file, string type, int order)> GetProjectImports(
        List<Import> imports,
        List<ProjectEvaluation> evaluations,
        string projectName,
        string? projectFile)
    {
        var result = new List<(string file, string type, int order)>();
        var order = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Get imports from both Import nodes and ProjectEvaluation children
        var allImports = imports
            .Where(i => MatchesProject(i, projectName, projectFile))
            .ToList();

        // Also check evaluation children
        foreach (var eval in evaluations)
        {
            var evalName = eval.Name ?? Path.GetFileNameWithoutExtension(eval.ProjectFile ?? "");
            if (!evalName.Equals(projectName, StringComparison.OrdinalIgnoreCase) &&
                eval.ProjectFile?.Equals(projectFile, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            foreach (var child in eval.Children)
            {
                if (child is Import import)
                {
                    allImports.Add(import);
                }
            }
        }

        foreach (var import in allImports)
        {
            var importedFile = import.ProjectFilePath ?? import.ImportedProjectFilePath ?? "";
            if (string.IsNullOrEmpty(importedFile) || seen.Contains(importedFile))
                continue;

            seen.Add(importedFile);
            var importType = importedFile.DetermineImportType();
            result.Add((importedFile, importType, ++order));
        }

        return result.OrderBy(r => r.order).ToList();
    }

    private static List<object> DetectPropertyConflicts(List<Property> properties)
    {
        // Find properties that were set multiple times with different values
        var conflicts = properties
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(p => p.Value).Distinct().Count() > 1)
            .Select(g =>
            {
                var assignments = g.ToList();
                var values = assignments.Select(p => p.Value).Distinct().ToList();

                return new
                {
                    property = g.Key,
                    valueCount = values.Count,
                    finalValue = assignments.Last().Value,
                    values = values.Select(v => TruncateValue(v, 80)).ToList()
                };
            })
            .OrderByDescending(c => c.valueCount)
            .Take(20)
            .Cast<object>()
            .ToList();

        return conflicts;
    }

    private static object BuildProjectMetadata(
        List<(string name, string? value, string? source)> properties,
        string projectName,
        string? projectFile)
    {
        var propDict = properties.ToDictionary(p => p.name, p => p.value, StringComparer.OrdinalIgnoreCase);

        return new
        {
            name = projectName,
            file = projectFile != null ? Path.GetFileName(projectFile) : null,
            sdk = propDict.GetValueOrDefault("UsingMicrosoftNETSdk") == "true" ? "Microsoft.NET.Sdk" :
                  propDict.GetValueOrDefault("MSBuildProjectExtension"),
            targetFramework = propDict.GetValueOrDefault("TargetFramework") ?? propDict.GetValueOrDefault("TargetFrameworks"),
            configuration = propDict.GetValueOrDefault("Configuration"),
            platform = propDict.GetValueOrDefault("Platform"),
            outputType = propDict.GetValueOrDefault("OutputType"),
            assemblyName = propDict.GetValueOrDefault("AssemblyName"),
            rootNamespace = propDict.GetValueOrDefault("RootNamespace"),
            langVersion = propDict.GetValueOrDefault("LangVersion"),
            nullable = propDict.GetValueOrDefault("Nullable"),
            implicitUsings = propDict.GetValueOrDefault("ImplicitUsings")
        };
    }

    private static List<(string name, string? value, string? source)> GetImportantProperties(
        List<(string name, string? value, string? source)> properties)
    {
        // Categories of important properties
        var importantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Project identity
            "AssemblyName", "RootNamespace", "ProjectGuid",

            // Framework/SDK
            "TargetFramework", "TargetFrameworks", "RuntimeIdentifier", "RuntimeIdentifiers",
            "UsingMicrosoftNETSdk", "MSBuildProjectExtension",

            // Build configuration
            "Configuration", "Platform", "OutputType",
            "OutputPath", "IntermediateOutputPath", "BaseOutputPath", "BaseIntermediateOutputPath",

            // Versioning
            "Version", "AssemblyVersion", "FileVersion", "PackageVersion", "InformationalVersion",

            // C# settings
            "LangVersion", "Nullable", "ImplicitUsings", "EnableDefaultItems",
            "TreatWarningsAsErrors", "WarningLevel", "NoWarn",

            // NuGet
            "PackageId", "IsPackable", "GeneratePackageOnBuild",

            // Publishing
            "PublishDir", "SelfContained", "PublishSingleFile", "PublishTrimmed",

            // References
            "EnableDefaultCompileItems", "EnableDefaultNoneItems",

            // Common paths
            "MSBuildProjectDirectory", "MSBuildProjectFile", "MSBuildThisFileDirectory"
        };

        return properties
            .Where(p => importantNames.Contains(p.name))
            .OrderBy(p => p.name)
            .ToList();
    }

    private static object CategorizeProperties(List<(string name, string? value, string? source)> properties)
    {
        var categories = new Dictionary<string, List<object>>
        {
            ["identity"] = [],
            ["framework"] = [],
            ["build"] = [],
            ["versioning"] = [],
            ["csharp"] = [],
            ["nuget"] = [],
            ["paths"] = [],
            ["other"] = []
        };

        var categoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Identity
            ["AssemblyName"] = "identity",
            ["RootNamespace"] = "identity",
            ["ProjectGuid"] = "identity",

            // Framework
            ["TargetFramework"] = "framework",
            ["TargetFrameworks"] = "framework",
            ["RuntimeIdentifier"] = "framework",
            ["RuntimeIdentifiers"] = "framework",
            ["UsingMicrosoftNETSdk"] = "framework",

            // Build
            ["Configuration"] = "build",
            ["Platform"] = "build",
            ["OutputType"] = "build",
            ["OutputPath"] = "build",
            ["IntermediateOutputPath"] = "build",
            ["BaseOutputPath"] = "build",
            ["BaseIntermediateOutputPath"] = "build",

            // Versioning
            ["Version"] = "versioning",
            ["AssemblyVersion"] = "versioning",
            ["FileVersion"] = "versioning",
            ["PackageVersion"] = "versioning",
            ["InformationalVersion"] = "versioning",

            // C#
            ["LangVersion"] = "csharp",
            ["Nullable"] = "csharp",
            ["ImplicitUsings"] = "csharp",
            ["TreatWarningsAsErrors"] = "csharp",
            ["WarningLevel"] = "csharp",
            ["NoWarn"] = "csharp",

            // NuGet
            ["PackageId"] = "nuget",
            ["IsPackable"] = "nuget",
            ["GeneratePackageOnBuild"] = "nuget",

            // Paths
            ["MSBuildProjectDirectory"] = "paths",
            ["MSBuildProjectFile"] = "paths",
            ["MSBuildThisFileDirectory"] = "paths",
            ["PublishDir"] = "paths"
        };

        foreach (var prop in properties)
        {
            var category = categoryMap.GetValueOrDefault(prop.name, "other");
            categories[category].Add(new
            {
                name = prop.name,
                value = TruncateValue(prop.value, 150),
                source = prop.source
            });
        }

        // Remove empty categories
        return categories
            .Where(kvp => kvp.Value.Count > 0)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }
}
