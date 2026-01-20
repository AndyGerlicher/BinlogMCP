using System.Text.Json;

namespace BinlogMcp.Formatting;

/// <summary>
/// Shared helper methods for formatters.
/// </summary>
internal static class FormatterHelpers
{
    /// <summary>
    /// Returns true if the element is a simple value (string, number, bool, null).
    /// </summary>
    public static bool IsSimpleValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => true,
        JsonValueKind.Number => true,
        JsonValueKind.True => true,
        JsonValueKind.False => true,
        JsonValueKind.Null => true,
        _ => false
    };

    /// <summary>
    /// Formats a JSON element as a plain string value.
    /// </summary>
    public static string FormatValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => FormatNumber(element),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        _ => element.ToString()
    };

    /// <summary>
    /// Formats a number element, preferring integer format when possible.
    /// </summary>
    public static string FormatNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var longVal))
            return longVal.ToString();
        return element.TryGetDouble(out var d) ? d.ToString("G") : element.ToString();
    }
}
