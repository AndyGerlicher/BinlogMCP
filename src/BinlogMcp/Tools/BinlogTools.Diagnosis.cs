using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace BinlogMcp.Tools;

public static partial class BinlogTools
{
    [McpServerTool, Description("Analyzes build failures and provides diagnosis with actionable fix suggestions")]
    public static string GetFailureDiagnosis(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Output format: json (default), markdown")] string format = "json")
    {
        var formatError = TryParseFormatWithError(format, out var outputFormat);
        if (formatError != null) return formatError;

        return ExecuteBinlogToolWithFormat(binlogPath, outputFormat, "Failure Diagnosis", build =>
        {
            var errors = new List<Error>();
            var warnings = new List<Warning>();

            build.VisitAllChildren<BaseNode>(node =>
            {
                switch (node)
                {
                    case Error e:
                        errors.Add(e);
                        break;
                    case Warning w:
                        warnings.Add(w);
                        break;
                }
            });

            if (build.Succeeded && errors.Count == 0)
            {
                return new
                {
                    file = binlogPath,
                    succeeded = true,
                    message = "Build succeeded with no errors",
                    warningCount = warnings.Count
                };
            }

            // Categorize errors
            var categorizedErrors = errors
                .GroupBy(e => CategorizeError(e))
                .Select(g => new
                {
                    category = g.Key,
                    count = g.Count(),
                    priority = GetCategoryPriority(g.Key),
                    description = GetCategoryDescription(g.Key),
                    suggestions = GetCategorySuggestions(g.Key, g.ToList()),
                    errors = g.Take(5).Select(e => new
                    {
                        code = e.Code,
                        message = e.Text,
                        file = e.File,
                        line = e.LineNumber,
                        project = Path.GetFileName(e.ProjectFile)
                    }).ToList()
                })
                .OrderBy(c => c.priority)
                .ToList();

            // Find root causes
            var rootCauses = FindRootCauses(errors);

            // Detect patterns
            var patterns = DetectFailurePatterns(errors, warnings);

            return new
            {
                file = binlogPath,
                succeeded = build.Succeeded,
                summary = new
                {
                    totalErrors = errors.Count,
                    totalWarnings = warnings.Count,
                    errorCategories = categorizedErrors.Count,
                    likelyRootCauses = rootCauses.Count
                },
                rootCauses,
                patterns = patterns.Count > 0 ? patterns : null,
                diagnoses = categorizedErrors
            };
        });
    }

    private static string CategorizeError(Error error)
    {
        var code = error.Code ?? "";
        var text = error.Text ?? "";

        // C# Compiler errors
        if (code.StartsWith("CS"))
        {
            return code switch
            {
                "CS0246" => "MissingType",
                "CS0234" => "MissingNamespace",
                "CS1061" => "MissingMember",
                "CS0103" => "UndefinedName",
                "CS0019" => "TypeMismatch",
                "CS0029" => "TypeConversion",
                "CS0117" => "MissingMember",
                "CS0535" => "InterfaceNotImplemented",
                "CS0012" => "MissingAssemblyReference",
                "CS0006" => "MissingMetadataFile",
                "CS1002" or "CS1003" or "CS1513" or "CS1514" => "SyntaxError",
                _ when code.StartsWith("CS0") => "CompilerError",
                _ when code.StartsWith("CS1") => "SyntaxError",
                _ => "CompilerError"
            };
        }

        // MSBuild errors
        if (code.StartsWith("MSB"))
        {
            return code switch
            {
                "MSB3073" => "ExecFailed",
                "MSB4018" => "TaskFailed",
                "MSB3202" => "ProjectNotFound",
                "MSB4057" => "TargetNotFound",
                _ => "MSBuildError"
            };
        }

        // NuGet errors
        if (code.StartsWith("NU"))
        {
            return "NuGetError";
        }

        // Check by message content
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
        {
            return "FileNotFound";
        }

        if (text.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("access denied", StringComparison.OrdinalIgnoreCase))
        {
            return "PermissionError";
        }

        return "Other";
    }

    private static int GetCategoryPriority(string category)
    {
        return category switch
        {
            "NuGetError" => 1,        // Fix package issues first
            "MissingAssemblyReference" => 2,
            "MissingMetadataFile" => 2,
            "ProjectNotFound" => 3,
            "FileNotFound" => 3,
            "MissingType" => 4,       // Often caused by above
            "MissingNamespace" => 4,
            "MissingMember" => 5,
            "SyntaxError" => 6,
            "TypeMismatch" => 7,
            "TypeConversion" => 7,
            "InterfaceNotImplemented" => 8,
            "CompilerError" => 9,
            "MSBuildError" => 10,
            "ExecFailed" => 11,
            "TaskFailed" => 11,
            "PermissionError" => 12,
            _ => 100
        };
    }

    private static string GetCategoryDescription(string category)
    {
        return category switch
        {
            "MissingType" => "Type or class cannot be found - usually missing using directive or reference",
            "MissingNamespace" => "Namespace does not exist - check project references and package imports",
            "MissingMember" => "Member (method/property) does not exist on the type",
            "UndefinedName" => "Name is not defined in current scope",
            "TypeMismatch" => "Operator cannot be applied to the given types",
            "TypeConversion" => "Cannot convert between types",
            "InterfaceNotImplemented" => "Class does not implement required interface members",
            "MissingAssemblyReference" => "Referenced assembly is missing from the project",
            "MissingMetadataFile" => "A required assembly or metadata file could not be found",
            "SyntaxError" => "Code has syntax errors (missing braces, semicolons, etc.)",
            "CompilerError" => "General C# compilation error",
            "NuGetError" => "NuGet package restore or reference issue",
            "MSBuildError" => "MSBuild configuration or target error",
            "ExecFailed" => "External command execution failed",
            "TaskFailed" => "MSBuild task threw an exception",
            "ProjectNotFound" => "Referenced project file cannot be found",
            "FileNotFound" => "Required file is missing",
            "PermissionError" => "Insufficient permissions to access file or resource",
            _ => "Build error"
        };
    }

    private static List<string> GetCategorySuggestions(string category, List<Error> errors)
    {
        var suggestions = new List<string>();

        switch (category)
        {
            case "MissingType":
            case "MissingNamespace":
                var missingTypes = errors
                    .Select(e => ExtractTypeName(e.Text))
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct()
                    .Take(3)
                    .ToList();
                if (missingTypes.Count > 0)
                {
                    suggestions.Add($"Add missing using directives for: {string.Join(", ", missingTypes)}");
                }
                suggestions.Add("Check if required NuGet packages are installed");
                suggestions.Add("Verify project references are correct");
                break;

            case "MissingAssemblyReference":
            case "MissingMetadataFile":
                suggestions.Add("Run 'dotnet restore' to restore packages");
                suggestions.Add("Check that all project references build successfully");
                suggestions.Add("Rebuild solution in dependency order");
                break;

            case "NuGetError":
                suggestions.Add("Run 'dotnet restore' to fix package issues");
                suggestions.Add("Check nuget.config for correct package sources");
                suggestions.Add("Verify package versions are compatible");
                break;

            case "SyntaxError":
                suggestions.Add("Check for missing semicolons, braces, or parentheses");
                suggestions.Add("Look for unclosed string literals or comments");
                break;

            case "ProjectNotFound":
            case "FileNotFound":
                var missingFiles = errors
                    .Select(e => e.File)
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Distinct()
                    .Take(3)
                    .ToList();
                if (missingFiles.Count > 0)
                {
                    suggestions.Add($"Check paths: {string.Join(", ", missingFiles)}");
                }
                suggestions.Add("Verify file paths in project references");
                break;

            case "ExecFailed":
            case "TaskFailed":
                suggestions.Add("Check the command output for specific error details");
                suggestions.Add("Verify required tools are installed and in PATH");
                break;

            case "InterfaceNotImplemented":
                suggestions.Add("Implement all required interface members");
                suggestions.Add("Consider using IDE quick-fix to generate implementations");
                break;

            default:
                suggestions.Add("Review error messages for specific details");
                break;
        }

        return suggestions;
    }

    private static string? ExtractTypeName(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText))
            return null;

        // Try to extract type name from common error patterns
        var patterns = new[] { "'([^']+)'", "\"([^\"]+)\"" };
        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(errorText, pattern);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }
        }
        return null;
    }

    private static List<object> FindRootCauses(List<Error> errors)
    {
        var rootCauses = new List<object>();

        // CS0006 (missing metadata file) often causes cascading CS0246 errors
        var metadataErrors = errors.Where(e => e.Code == "CS0006").ToList();
        if (metadataErrors.Count > 0)
        {
            rootCauses.Add(new
            {
                type = "MissingAssembly",
                description = "Missing assembly/metadata file causing cascading type resolution errors",
                affectedFiles = metadataErrors.Select(e => e.Text).Distinct().Take(5).ToList(),
                suggestion = "Rebuild dependencies first, or run 'dotnet restore'"
            });
        }

        // NU* errors block the whole build
        var nugetErrors = errors.Where(e => e.Code?.StartsWith("NU") == true).ToList();
        if (nugetErrors.Count > 0)
        {
            rootCauses.Add(new
            {
                type = "NuGetFailure",
                description = "NuGet restore failed - this blocks compilation",
                packages = nugetErrors.Select(e => e.Text).Distinct().Take(5).ToList(),
                suggestion = "Fix NuGet issues first: check package sources, versions, and network connectivity"
            });
        }

        // MSB3202 (project not found) causes dependent projects to fail
        var projectNotFound = errors.Where(e => e.Code == "MSB3202").ToList();
        if (projectNotFound.Count > 0)
        {
            rootCauses.Add(new
            {
                type = "MissingProject",
                description = "Referenced project not found - dependent projects will fail",
                projects = projectNotFound.Select(e => e.File).Distinct().Take(5).ToList(),
                suggestion = "Check project paths in .csproj files"
            });
        }

        return rootCauses;
    }

    private static List<object> DetectFailurePatterns(List<Error> errors, List<Warning> warnings)
    {
        var patterns = new List<object>();

        // Pattern: Many CS0246 errors from same project = likely missing package/reference
        var typeErrorsByProject = errors
            .Where(e => e.Code == "CS0246")
            .GroupBy(e => e.ProjectFile)
            .Where(g => g.Count() >= 3)
            .ToList();

        foreach (var group in typeErrorsByProject)
        {
            patterns.Add(new
            {
                pattern = "MissingDependencyCluster",
                project = Path.GetFileName(group.Key),
                description = $"Multiple missing type errors ({group.Count()}) suggest a missing package or project reference",
                suggestion = "Check PackageReferences and ProjectReferences in this project"
            });
        }

        // Pattern: CS0006 followed by many CS0246 = rebuild needed
        if (errors.Any(e => e.Code == "CS0006") && errors.Count(e => e.Code == "CS0246") > 5)
        {
            patterns.Add(new
            {
                pattern = "CascadingFromMissingAssembly",
                description = "Missing assembly is causing cascading type resolution failures",
                suggestion = "Rebuild dependencies in correct order, or clean and rebuild entire solution"
            });
        }

        // Pattern: Same error code many times = systematic issue
        var repeatedErrors = errors
            .GroupBy(e => e.Code)
            .Where(g => g.Count() >= 10)
            .ToList();

        foreach (var group in repeatedErrors)
        {
            patterns.Add(new
            {
                pattern = "RepeatedError",
                errorCode = group.Key,
                count = group.Count(),
                description = $"Error {group.Key} appears {group.Count()} times - likely a systematic issue",
                suggestion = "Fix the root cause rather than each instance individually"
            });
        }

        return patterns;
    }
}
