using System.Text.Json;

namespace BinlogMcp.Formatting;

/// <summary>
/// Main entry point for formatting tool output in different formats.
/// </summary>
public static class OutputFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Formats data according to the specified output format.
    /// </summary>
    /// <param name="data">The data object to format.</param>
    /// <param name="format">The desired output format.</param>
    /// <param name="title">Optional title for markdown/timeline output.</param>
    /// <param name="options">Optional formatting options.</param>
    /// <returns>Formatted string output.</returns>
    public static string Format(object data, OutputFormat format, string? title = null, FormatOptions? options = null)
    {
        options ??= FormatOptions.Default;
        if (title != null)
            options = options with { Title = title };

        return format switch
        {
            OutputFormat.Json => FormatAsJson(data),
            OutputFormat.Markdown => MarkdownFormatter.Format(data, options),
            OutputFormat.Csv => CsvFormatter.Format(data, options),
            OutputFormat.Timeline => TimelineFormatter.Format(data, options),
            _ => FormatAsJson(data)
        };
    }

    /// <summary>
    /// Tries to parse a format string into an OutputFormat enum.
    /// </summary>
    /// <param name="format">The format string (e.g., "json", "markdown", "csv", "timeline").</param>
    /// <param name="outputFormat">The parsed OutputFormat if successful.</param>
    /// <returns>True if parsing succeeded, false otherwise.</returns>
    public static bool TryParseFormat(string? format, out OutputFormat outputFormat)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            outputFormat = OutputFormat.Json;
            return true;
        }

        var normalized = format.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "json":
                outputFormat = OutputFormat.Json;
                return true;
            case "markdown":
            case "md":
                outputFormat = OutputFormat.Markdown;
                return true;
            case "csv":
                outputFormat = OutputFormat.Csv;
                return true;
            case "timeline":
                outputFormat = OutputFormat.Timeline;
                return true;
            default:
                outputFormat = OutputFormat.Json;
                return false;
        }
    }

    private static string FormatAsJson(object data)
    {
        return JsonSerializer.Serialize(data, JsonOptions);
    }
}
