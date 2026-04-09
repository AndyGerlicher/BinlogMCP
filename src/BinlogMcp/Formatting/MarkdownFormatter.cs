using System.Text;
using System.Text.Json;

namespace BinlogMcp.Formatting;

/// <summary>
/// Formats data as Markdown with tables and bullet lists.
/// </summary>
public static class MarkdownFormatter
{
    /// <summary>
    /// Formats an object as Markdown.
    /// </summary>
    public static string Format(object data, FormatOptions? options = null)
    {
        options ??= FormatOptions.Default;
        var sb = new StringBuilder();

        // Convert to JsonElement for uniform handling
        var json = JsonSerializer.SerializeToElement(data);
        FormatElement(sb, json, options.Title, 1);

        return sb.ToString().TrimEnd();
    }

    private static void FormatElement(StringBuilder sb, JsonElement element, string? title, int headingLevel)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                FormatObject(sb, element, title, headingLevel);
                break;
            case JsonValueKind.Array:
                FormatArray(sb, element, title, headingLevel);
                break;
            default:
                sb.AppendLine(element.ToString());
                break;
        }
    }

    private static void FormatObject(StringBuilder sb, JsonElement obj, string? title, int headingLevel)
    {
        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine($"{new string('#', headingLevel)} {title}");
            sb.AppendLine();
        }

        var properties = obj.EnumerateObject().ToList();
        var simpleProps = new List<JsonProperty>();
        var complexProps = new List<JsonProperty>();

        foreach (var prop in properties)
        {
            if (IsSimpleValue(prop.Value))
                simpleProps.Add(prop);
            else
                complexProps.Add(prop);
        }

        // Format simple properties as bullet list
        if (simpleProps.Count > 0)
        {
            foreach (var prop in simpleProps)
            {
                var name = FormatPropertyName(prop.Name);
                var value = FormatValue(prop.Value);
                sb.AppendLine($"- **{name}**: {value}");
            }
            sb.AppendLine();
        }

        // Format complex properties (arrays/objects) as subsections
        foreach (var prop in complexProps)
        {
            var name = FormatPropertyName(prop.Name);
            var nextLevel = Math.Min(headingLevel + 1, 6);

            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                FormatArray(sb, prop.Value, name, nextLevel);
            }
            else if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                FormatElement(sb, prop.Value, name, nextLevel);
            }
        }
    }

    private static void FormatArray(StringBuilder sb, JsonElement array, string? title, int headingLevel)
    {
        var items = array.EnumerateArray().ToList();

        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine($"{new string('#', headingLevel)} {title}");
            sb.AppendLine();
        }

        if (items.Count == 0)
        {
            sb.AppendLine("*No items*");
            sb.AppendLine();
            return;
        }

        // Check if items are objects with consistent properties
        if (items.All(i => i.ValueKind == JsonValueKind.Object))
        {
            var firstItem = items[0];
            var allProps = firstItem.EnumerateObject().ToList();

            // Check if all items have similar structure and mostly simple values
            var simpleProps = allProps.Where(p => IsSimpleValue(p.Value)).Select(p => p.Name).ToList();

            if (simpleProps.Count >= 2)
            {
                // Format as table
                FormatAsTable(sb, items, simpleProps);
                sb.AppendLine();
                return;
            }
        }

        // Fall back to bullet list for simple arrays or complex structures
        foreach (var item in items)
        {
            if (IsSimpleValue(item))
            {
                sb.AppendLine($"- {FormatValue(item)}");
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                // Compact object representation
                var props = item.EnumerateObject()
                    .Where(p => IsSimpleValue(p.Value))
                    .Take(5)
                    .Select(p => $"{FormatPropertyName(p.Name)}: {FormatValue(p.Value)}");
                sb.AppendLine($"- {string.Join(", ", props)}");
            }
        }
        sb.AppendLine();
    }

    private static void FormatAsTable(StringBuilder sb, List<JsonElement> items, List<string> columns)
    {
        // Header
        var headers = columns.Select(FormatPropertyName);
        sb.AppendLine($"| {string.Join(" | ", headers)} |");

        // Separator
        var separator = columns.Select(_ => "---");
        sb.AppendLine($"| {string.Join(" | ", separator)} |");

        // Rows
        foreach (var item in items)
        {
            var values = columns.Select(col =>
            {
                if (item.TryGetProperty(col, out var value))
                    return EscapeTableCell(FormatValue(value));
                return "";
            });
            sb.AppendLine($"| {string.Join(" | ", values)} |");
        }
    }

    private static bool IsSimpleValue(JsonElement element) => FormatterHelpers.IsSimpleValue(element);

    private static string FormatValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => FormatNumber(element),
            JsonValueKind.True => "Yes",
            JsonValueKind.False => "No",
            JsonValueKind.Null => "*null*",
            _ => element.ToString()
        };
    }

    private static string FormatNumber(JsonElement element)
    {
        // Try to format as integer if possible
        if (element.TryGetInt64(out var longVal))
            return longVal.ToString();

        var doubleVal = element.GetDouble();

        // Format durations nicely
        if (doubleVal >= 1000 && doubleVal < 60000)
            return $"{doubleVal / 1000:0.##}s";

        return doubleVal.ToString("0.##");
    }

    /// <summary>
    /// Converts camelCase or PascalCase property names to Title Case with spaces.
    /// </summary>
    public static string FormatPropertyName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        var sb = new StringBuilder();
        sb.Append(char.ToUpper(name[0]));

        for (int i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                sb.Append(' ');
            }
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string EscapeTableCell(string value)
    {
        // Escape pipe characters and newlines in table cells
        return value.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
    }
}
