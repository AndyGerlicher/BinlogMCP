namespace BinlogMcp.Formatting;

/// <summary>
/// Configuration options for output formatting.
/// </summary>
public record FormatOptions
{
    /// <summary>
    /// Common array property names used by formatters to find data arrays.
    /// </summary>
    internal static readonly string[] CommonArrayPropertyNames =
        ["items", "errors", "warnings", "targets", "tasks", "projects",
         "properties", "changes", "events", "added", "removed", "changed"];

    /// <summary>
    /// CSV field delimiter. Default is comma.
    /// </summary>
    public char CsvDelimiter { get; set; } = ',';

    /// <summary>
    /// Maximum depth for flattening nested objects in CSV output.
    /// </summary>
    public int MaxFlattenDepth { get; set; } = 2;

    /// <summary>
    /// Whether to include headers in CSV output.
    /// </summary>
    public bool IncludeCsvHeaders { get; set; } = true;

    /// <summary>
    /// Title for markdown/timeline output.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Default options instance.
    /// </summary>
    public static FormatOptions Default { get; } = new();
}
