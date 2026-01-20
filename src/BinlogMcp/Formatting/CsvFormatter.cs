using System.Text;
using System.Text.Json;

namespace BinlogMcp.Formatting;

/// <summary>
/// Formats data as CSV with automatic array detection and flattening.
/// </summary>
public static class CsvFormatter
{
    /// <summary>
    /// Formats an object as CSV. Automatically finds the primary array to export.
    /// </summary>
    public static string Format(object data, FormatOptions? options = null)
    {
        options ??= FormatOptions.Default;
        var delimiter = options.CsvDelimiter;

        // Convert to JsonElement for uniform handling
        var json = JsonSerializer.SerializeToElement(data);

        // Find the primary array to export
        var arrayToExport = FindPrimaryArray(json);
        if (arrayToExport == null || arrayToExport.Value.GetArrayLength() == 0)
        {
            return "# No tabular data found";
        }

        var items = arrayToExport.Value.EnumerateArray().ToList();

        // Get columns from the first item
        if (items[0].ValueKind != JsonValueKind.Object)
        {
            // Simple array of values
            return string.Join(Environment.NewLine, items.Select(i => EscapeField(i.ToString(), delimiter)));
        }

        // Get all unique column names, preserving order from first item
        var columns = GetFlattenedColumns(items[0], options.MaxFlattenDepth);

        var sb = new StringBuilder();

        // Header row
        if (options.IncludeCsvHeaders)
        {
            sb.AppendLine(string.Join(delimiter, columns.Select(c => EscapeField(c, delimiter))));
        }

        // Data rows
        foreach (var item in items)
        {
            var values = columns.Select(col => GetFlattenedValue(item, col, delimiter));
            sb.AppendLine(string.Join(delimiter, values));
        }

        return sb.ToString().TrimEnd();
    }

    private static JsonElement? FindPrimaryArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element;

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        // Look for common array property names
        foreach (var name in FormatOptions.CommonArrayPropertyNames)
        {
            if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Array)
                return prop;
        }

        // Fall back to first array property
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Array)
                return prop.Value;
        }

        return null;
    }

    private static List<string> GetFlattenedColumns(JsonElement item, int maxDepth)
    {
        var columns = new List<string>();
        CollectColumns(item, "", columns, 0, maxDepth);
        return columns;
    }

    private static void CollectColumns(JsonElement element, string prefix, List<string> columns, int depth, int maxDepth)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var prop in element.EnumerateObject())
        {
            var columnName = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}.{prop.Name}";

            if (prop.Value.ValueKind == JsonValueKind.Object && depth < maxDepth)
            {
                CollectColumns(prop.Value, columnName, columns, depth + 1, maxDepth);
            }
            else if (prop.Value.ValueKind != JsonValueKind.Array)
            {
                columns.Add(columnName);
            }
            // Skip arrays in CSV - they don't flatten well
        }
    }

    private static string GetFlattenedValue(JsonElement item, string columnPath, char delimiter)
    {
        var parts = columnPath.Split('.');
        var current = item;

        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object)
                return "";

            if (!current.TryGetProperty(part, out current))
                return "";
        }

        return EscapeField(FormatValue(current), delimiter);
    }

    private static string FormatValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "[object]",
        JsonValueKind.Array => "[array]",
        _ => FormatterHelpers.FormatValue(element)
    };

    private static string EscapeField(string value, char delimiter)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // Fields containing delimiter, quotes, or newlines need to be quoted
        bool needsQuoting = value.Contains(delimiter) ||
                           value.Contains('"') ||
                           value.Contains('\n') ||
                           value.Contains('\r');

        if (needsQuoting)
        {
            // Double up any existing quotes and wrap in quotes
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
