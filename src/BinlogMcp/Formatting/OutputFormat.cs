namespace BinlogMcp.Formatting;

/// <summary>
/// Output format options for binlog tool results.
/// </summary>
public enum OutputFormat
{
    /// <summary>
    /// JSON format (default). Structured data for programmatic consumption.
    /// </summary>
    Json,

    /// <summary>
    /// Markdown format. Human-readable reports with tables and bullet lists.
    /// </summary>
    Markdown,

    /// <summary>
    /// CSV format. Tabular data for spreadsheet import.
    /// </summary>
    Csv,

    /// <summary>
    /// Timeline JSON format. Events with timing data for visualization.
    /// </summary>
    Timeline
}
