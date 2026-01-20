using System.Text.RegularExpressions;

namespace BinlogMcp.Tools;

/// <summary>
/// Extension methods to reduce code duplication across BinlogTools.
/// </summary>
internal static class BinlogExtensions
{
    #region String Filtering

    /// <summary>
    /// Returns true if the value matches the filter (contains, case-insensitive).
    /// Returns true if filter is null/empty (no filtering).
    /// </summary>
    public static bool MatchesFilter(this string? value, string? filter)
        => string.IsNullOrEmpty(filter) || (value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Returns true if the filter is set and the value does NOT match it.
    /// Use in continue/skip patterns: if (value.FailsFilter(filter)) continue;
    /// </summary>
    public static bool FailsFilter(this string? value, string? filter)
        => !string.IsNullOrEmpty(filter) && !(value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);

    #endregion

    #region DateTime Formatting

    /// <summary>
    /// Formats DateTime as time only: "HH:mm:ss.fff"
    /// </summary>
    public static string ToTimeString(this DateTime dt) => dt.ToString("HH:mm:ss.fff");

    /// <summary>
    /// Formats DateTime as date and time: "yyyy-MM-dd HH:mm:ss"
    /// </summary>
    public static string ToDateTimeString(this DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm:ss");

    #endregion

    #region Message Extraction

    private static readonly Regex TargetNamePattern = new(
        @"[Tt]arget\s+['""]?(\w+)['""]?",
        RegexOptions.Compiled);

    private static readonly Regex SkippingTargetPattern = new(
        @"[Ss]kipping\s+target\s+['""]?(\w+)['""]?",
        RegexOptions.Compiled);

    private static readonly Regex QuotedTargetSkipPattern = new(
        @"['""](\w+)['""].*(?:skipped|skip)",
        RegexOptions.Compiled);

    private static readonly Regex ConditionTargetPattern = new(
        @"[Tt]arget\s+['""]?(\w+)['""]?.*condition",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BeforeAfterSourcePattern = new(
        @"[Tt]arget\s+['""]?(\w+)['""]?",
        RegexOptions.Compiled | RegexOptions.RightToLeft);

    private static readonly Regex BeforeAfterTriggerPattern = new(
        @"=\s*['""]?(\w+)['""]?",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts target name from various message formats.
    /// </summary>
    public static string? ExtractTargetName(this string message)
    {
        var match = TargetNamePattern.Match(message);
        if (match.Success) return match.Groups[1].Value;

        match = SkippingTargetPattern.Match(message);
        if (match.Success) return match.Groups[1].Value;

        match = QuotedTargetSkipPattern.Match(message);
        if (match.Success) return match.Groups[1].Value;

        return null;
    }

    /// <summary>
    /// Extracts target name from condition-related messages.
    /// </summary>
    public static string? ExtractTargetNameFromCondition(this string message)
    {
        var match = ConditionTargetPattern.Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Extracts source and trigger targets from BeforeTargets/AfterTargets messages.
    /// </summary>
    public static (string? sourceTarget, string? triggerTarget) ExtractBeforeAfterTargets(
        this string message, string keyword)
    {
        var keywordIndex = message.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (keywordIndex < 0) return (null, null);

        var beforeKeyword = message[..keywordIndex];
        var afterKeyword = message[keywordIndex..];

        var sourceMatch = BeforeAfterSourcePattern.Match(beforeKeyword);
        var triggerMatch = BeforeAfterTriggerPattern.Match(afterKeyword);

        return (
            sourceMatch.Success ? sourceMatch.Groups[1].Value : null,
            triggerMatch.Success ? triggerMatch.Groups[1].Value : null
        );
    }

    /// <summary>
    /// Determines the skip reason from a message.
    /// </summary>
    public static string DetermineSkipReason(this string message)
    {
        var lower = message.ToLowerInvariant();

        if (lower.Contains("condition") && lower.Contains("false"))
            return "Condition evaluated to false";
        if (lower.Contains("up-to-date") || lower.Contains("up to date"))
            return "Outputs are up-to-date";
        if (lower.Contains("previously") || lower.Contains("already"))
            return "Already executed";
        if (lower.Contains("not defined") || lower.Contains("does not exist"))
            return "Target not defined";
        if (lower.Contains("empty"))
            return "No items to process";

        return "Skipped";
    }

    #endregion

    #region Import Type Detection

    /// <summary>
    /// Determines the type of import based on file path.
    /// </summary>
    public static string DetermineImportType(this string? filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return "Unknown";

        var fileName = Path.GetFileName(filePath);
        var lower = filePath.ToLowerInvariant();

        return lower switch
        {
            _ when lower.Contains("sdk") || lower.Contains("\\dotnet\\") || lower.Contains("/dotnet/") => "SDK",
            _ when fileName.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase) => "Directory.Build",
            _ when fileName.StartsWith("Directory.Packages.", StringComparison.OrdinalIgnoreCase) => "Central Package Management",
            _ when lower.Contains("nuget") || lower.Contains(".nuget") || lower.Contains("packages") => "NuGet Package",
            _ when fileName.EndsWith(".props", StringComparison.OrdinalIgnoreCase) => "Props File",
            _ when fileName.EndsWith(".targets", StringComparison.OrdinalIgnoreCase) => "Targets File",
            _ when fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                   fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) => "Project Reference",
            _ => "Other"
        };
    }

    #endregion

    #region Set Comparison

    /// <summary>
    /// Result of comparing two sets of items.
    /// </summary>
    public class DiffResult<T>
    {
        public required List<T> Added { get; init; }
        public required List<T> Removed { get; init; }
        public required List<(T Baseline, T Comparison)> Changed { get; init; }
        public int UnchangedCount { get; init; }
    }

    /// <summary>
    /// Compares two collections and returns items that were added, removed, or changed.
    /// </summary>
    /// <typeparam name="T">Item type</typeparam>
    /// <typeparam name="TKey">Key type for matching items</typeparam>
    /// <param name="baseline">Original collection</param>
    /// <param name="comparison">New collection</param>
    /// <param name="keySelector">Function to extract comparison key</param>
    /// <param name="hasChanged">Optional function to detect if matched items have changed (default: always false)</param>
    public static DiffResult<T> Diff<T, TKey>(
        this IEnumerable<T> baseline,
        IEnumerable<T> comparison,
        Func<T, TKey> keySelector,
        Func<T, T, bool>? hasChanged = null) where TKey : notnull
    {
        var baselineDict = baseline.ToDictionary(keySelector);
        var comparisonDict = comparison.ToDictionary(keySelector);

        var added = new List<T>();
        var removed = new List<T>();
        var changed = new List<(T, T)>();
        var unchangedCount = 0;

        // Find added and changed
        foreach (var kvp in comparisonDict)
        {
            if (baselineDict.TryGetValue(kvp.Key, out var baseItem))
            {
                if (hasChanged?.Invoke(baseItem, kvp.Value) == true)
                    changed.Add((baseItem, kvp.Value));
                else
                    unchangedCount++;
            }
            else
            {
                added.Add(kvp.Value);
            }
        }

        // Find removed
        foreach (var kvp in baselineDict)
        {
            if (!comparisonDict.ContainsKey(kvp.Key))
                removed.Add(kvp.Value);
        }

        return new DiffResult<T>
        {
            Added = added,
            Removed = removed,
            Changed = changed,
            UnchangedCount = unchangedCount
        };
    }

    #endregion
}
