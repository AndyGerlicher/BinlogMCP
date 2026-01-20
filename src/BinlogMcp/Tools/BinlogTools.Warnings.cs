using Microsoft.Build.Logging.StructuredLogger;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace BinlogMcp.Tools;

/// <summary>
/// Warning analysis tools - trends, categorization, suppression suggestions.
/// </summary>
public static partial class BinlogTools
{
    [McpServerTool, Description("Analyzes warning trends - categorizes warnings by type, suggests bulk fixes or suppressions, identifies most common warning patterns")]
    public static string GetWarningTrendsAnalysis(
        [Description("Path to the binlog file")] string binlogPath,
        [Description("Minimum count to include in trends (default: 1)")] int minCount = 1)
    {
        return ExecuteBinlogTool(binlogPath, build =>
        {
            var warnings = new List<Warning>();
            build.VisitAllChildren<Warning>(w => warnings.Add(w));

            // Group by warning code
            var byCode = warnings
                .GroupBy(w => w.Code ?? "Unknown")
                .Select(g => new
                {
                    code = g.Key,
                    count = g.Count(),
                    message = TruncateValue(g.First().Text, 200),
                    projects = g.Select(w => Path.GetFileName(w.ProjectFile)).Distinct().ToList(),
                    files = g.Select(w => w.File).Where(f => !string.IsNullOrEmpty(f)).Distinct().Take(10).ToList()
                })
                .Where(x => x.count >= minCount)
                .OrderByDescending(x => x.count)
                .ToList();

            // Group by project
            var byProject = warnings
                .GroupBy(w => Path.GetFileName(w.ProjectFile) ?? "Unknown")
                .Select(g => new
                {
                    project = g.Key,
                    count = g.Count(),
                    topCodes = g.GroupBy(w => w.Code ?? "Unknown")
                        .OrderByDescending(cg => cg.Count())
                        .Take(5)
                        .Select(cg => new { code = cg.Key, count = cg.Count() })
                        .ToList()
                })
                .Where(x => x.count >= minCount)
                .OrderByDescending(x => x.count)
                .ToList();

            // Categorize warnings
            var categories = CategorizeWarnings(warnings);

            // Generate suppression suggestions for most common warnings
            var suppressionSuggestions = byCode
                .Where(w => w.count >= 3)
                .Take(10)
                .Select(w => new
                {
                    code = w.code,
                    count = w.count,
                    globalSuppression = GetGlobalSuppression(w.code),
                    pragmaSuppression = GetPragmaSuppression(w.code),
                    editorConfigRule = GetEditorConfigRule(w.code),
                    recommendation = GetSuppressionRecommendation(w.code, w.count, w.projects.Count)
                })
                .ToList();

            // Find fix suggestions for common warning types
            var fixSuggestions = byCode
                .Where(w => !string.IsNullOrEmpty(w.code) && w.code != "Unknown")
                .Take(15)
                .Select(w => new
                {
                    code = w.code,
                    count = w.count,
                    suggestion = GetWarningFixSuggestion(w.code)
                })
                .Where(x => !string.IsNullOrEmpty(x.suggestion))
                .ToList();

            // Warning trends by file extension
            var byFileType = warnings
                .Where(w => !string.IsNullOrEmpty(w.File))
                .GroupBy(w => Path.GetExtension(w.File)?.ToLowerInvariant() ?? "unknown")
                .Select(g => new
                {
                    extension = g.Key,
                    count = g.Count(),
                    topCodes = g.GroupBy(w => w.Code ?? "Unknown")
                        .OrderByDescending(cg => cg.Count())
                        .Take(3)
                        .Select(cg => new { code = cg.Key, count = cg.Count() })
                        .ToList()
                })
                .OrderByDescending(x => x.count)
                .ToList();

            return new
            {
                file = binlogPath,
                minCount,
                summary = new
                {
                    totalWarnings = warnings.Count,
                    uniqueCodes = byCode.Count,
                    projectsWithWarnings = byProject.Count,
                    suppressableCodes = suppressionSuggestions.Count,
                    fixableCodes = fixSuggestions.Count
                },
                categories,
                byWarningCode = byCode.Take(30).ToList(),
                byProject = byProject.Take(20).ToList(),
                byFileType = byFileType.Take(10).ToList(),
                suppressionSuggestions = suppressionSuggestions.Count > 0 ? suppressionSuggestions : null,
                fixSuggestions = fixSuggestions.Count > 0 ? fixSuggestions : null
            };
        });
    }

    private static object CategorizeWarnings(List<Warning> warnings)
    {
        var codeAnalysis = warnings.Where(w => w.Code != null && (
            w.Code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) ||
            w.Code.StartsWith("IDE", StringComparison.OrdinalIgnoreCase))).ToList();

        var compiler = warnings.Where(w => w.Code != null && (
            w.Code.StartsWith("CS", StringComparison.OrdinalIgnoreCase) ||
            w.Code.StartsWith("VB", StringComparison.OrdinalIgnoreCase) ||
            w.Code.StartsWith("FS", StringComparison.OrdinalIgnoreCase))).ToList();

        var msbuild = warnings.Where(w => w.Code != null &&
            w.Code.StartsWith("MSB", StringComparison.OrdinalIgnoreCase)).ToList();

        var nuget = warnings.Where(w => w.Code != null &&
            w.Code.StartsWith("NU", StringComparison.OrdinalIgnoreCase)).ToList();

        var security = warnings.Where(w => w.Code != null && (
            w.Code.StartsWith("SEC", StringComparison.OrdinalIgnoreCase) ||
            w.Code.StartsWith("SCS", StringComparison.OrdinalIgnoreCase) ||
            (w.Code.StartsWith("CA", StringComparison.OrdinalIgnoreCase) &&
             w.Text != null && w.Text.Contains("security", StringComparison.OrdinalIgnoreCase)))).ToList();

        var nullable = warnings.Where(w => w.Code != null && (
            w.Code.StartsWith("CS86", StringComparison.OrdinalIgnoreCase) ||
            w.Code.StartsWith("CS87", StringComparison.OrdinalIgnoreCase))).ToList();

        var other = warnings.Except(codeAnalysis).Except(compiler).Except(msbuild)
            .Except(nuget).Except(security).Except(nullable).ToList();

        return new
        {
            codeAnalysis = new { count = codeAnalysis.Count, topCodes = GetTopWarningCodes(codeAnalysis, 5) },
            compiler = new { count = compiler.Count, topCodes = GetTopWarningCodes(compiler, 5) },
            msbuild = new { count = msbuild.Count, topCodes = GetTopWarningCodes(msbuild, 5) },
            nuget = new { count = nuget.Count, topCodes = GetTopWarningCodes(nuget, 5) },
            security = new { count = security.Count, topCodes = GetTopWarningCodes(security, 5) },
            nullable = new { count = nullable.Count, topCodes = GetTopWarningCodes(nullable, 5) },
            other = new { count = other.Count, topCodes = GetTopWarningCodes(other, 5) }
        };
    }

    private static List<object> GetTopWarningCodes(List<Warning> warnings, int count)
    {
        return warnings
            .GroupBy(w => w.Code ?? "Unknown")
            .OrderByDescending(g => g.Count())
            .Take(count)
            .Select(g => new { code = (object)g.Key, count = (object)g.Count() })
            .ToList<object>();
    }

    private static string GetGlobalSuppression(string code)
    {
        return $"[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Category\", \"{code}\")]";
    }

    private static string GetPragmaSuppression(string code)
    {
        return $"#pragma warning disable {code}";
    }

    private static string GetEditorConfigRule(string code)
    {
        return $"dotnet_diagnostic.{code}.severity = none";
    }

    private static string GetSuppressionRecommendation(string code, int count, int projectCount)
    {
        if (projectCount > 3 && count > 20)
            return "Consider adding to .editorconfig at solution level";
        if (count > 10)
            return "Consider bulk suppression via .editorconfig";
        if (count > 5)
            return "Review and fix or suppress per-file";
        return "Fix individually or suppress if intentional";
    }

    private static string? GetWarningFixSuggestion(string code)
    {
        var suggestions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Nullable reference type warnings
            ["CS8600"] = "Initialize variable or use nullable type (Type?)",
            ["CS8601"] = "Add null check before dereferencing",
            ["CS8602"] = "Add null check or use null-conditional operator (?.)",
            ["CS8603"] = "Return a non-null value or change return type to nullable",
            ["CS8604"] = "Add null check for argument or make parameter nullable",
            ["CS8618"] = "Initialize non-nullable field in constructor or make nullable",
            ["CS8619"] = "Use correct nullability annotation",
            ["CS8625"] = "Pass non-null value or change parameter to nullable",
            ["CS8767"] = "Add nullability annotation to match overridden member",

            // Common compiler warnings
            ["CS0168"] = "Remove unused variable or use discard (_)",
            ["CS0169"] = "Remove unused field or add usage",
            ["CS0219"] = "Remove unused variable assignment",
            ["CS0414"] = "Remove unused field or add usage",
            ["CS0649"] = "Initialize field in constructor or mark as nullable",
            ["CS0162"] = "Remove unreachable code",
            ["CS0612"] = "Update to non-obsolete API or suppress if intentional",
            ["CS0618"] = "Update to non-obsolete API or suppress if intentional",
            ["CS1591"] = "Add XML documentation comment or disable documentation generation",
            ["CS1998"] = "Add await or remove async modifier",

            // Code analysis warnings
            ["CA1031"] = "Catch specific exception types instead of base Exception",
            ["CA1062"] = "Add null check for public method parameter",
            ["CA1303"] = "Pass literal as parameter or use resource string",
            ["CA1304"] = "Specify CultureInfo for string operations",
            ["CA1305"] = "Specify IFormatProvider for formatting operations",
            ["CA1307"] = "Specify StringComparison for string comparison",
            ["CA1308"] = "Use ToUpperInvariant instead of ToLowerInvariant",
            ["CA1816"] = "Call GC.SuppressFinalize in Dispose implementation",
            ["CA1822"] = "Mark member as static if it doesn't access instance data",
            ["CA2000"] = "Dispose IDisposable objects before losing scope",
            ["CA2007"] = "Call ConfigureAwait(false) on awaited task",
            ["CA2227"] = "Make collection property read-only or remove setter",

            // IDE suggestions
            ["IDE0001"] = "Simplify type name",
            ["IDE0002"] = "Simplify member access",
            ["IDE0003"] = "Remove 'this' qualification",
            ["IDE0004"] = "Remove unnecessary cast",
            ["IDE0005"] = "Remove unnecessary using directive",
            ["IDE0044"] = "Mark field as readonly",
            ["IDE0051"] = "Remove unused private member",
            ["IDE0052"] = "Remove unused private member value",
            ["IDE0059"] = "Remove unnecessary value assignment",
            ["IDE0060"] = "Remove unused parameter",
            ["IDE0161"] = "Use file-scoped namespace",

            // MSBuild warnings
            ["MSB3277"] = "Resolve assembly version conflicts - check binding redirects",
            ["MSB3243"] = "Reference conflict - consolidate package versions",
            ["MSB3270"] = "Processor architecture mismatch - align project settings",
            ["MSB3276"] = "Multiple assemblies with same identity - remove duplicates",

            // NuGet warnings
            ["NU1603"] = "Update package to supported version",
            ["NU1605"] = "Package downgrade detected - consolidate versions",
            ["NU1608"] = "Package version outside dependency constraint",
            ["NU1701"] = "Package uses assets for different framework - update package",
            ["NU1702"] = "Package uses assets for different runtime - update package"
        };

        return suggestions.TryGetValue(code, out var suggestion) ? suggestion : null;
    }
}
