namespace BinlogMcp.Visualization;

/// <summary>
/// Generates self-contained HTML pages with embedded Chart.js for visualizations.
/// </summary>
public static class HtmlTemplate
{
    // Chart.js CDN - we embed inline for offline use would require bundling,
    // so we use CDN for simplicity. Could be enhanced later.
    private const string ChartJsCdn = "https://cdn.jsdelivr.net/npm/chart.js@4.4.1/dist/chart.umd.min.js";
    private const string ChartJsAdapterCdn = "https://cdn.jsdelivr.net/npm/chartjs-adapter-date-fns@3.0.0/dist/chartjs-adapter-date-fns.bundle.min.js";

    /// <summary>
    /// Wraps chart content in a complete HTML page.
    /// </summary>
    public static string WrapInHtmlPage(string title, string bodyContent, string? additionalScripts = null)
    {
        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
                <meta charset="UTF-8">
                <meta name="viewport" content="width=device-width, initial-scale=1.0">
                <title>{EscapeHtml(title)}</title>
                <script src="{ChartJsCdn}"></script>
                <script src="{ChartJsAdapterCdn}"></script>
                <style>
                    {GetBaseStyles()}
                </style>
            </head>
            <body>
                <div class="container">
                    {bodyContent}
                </div>
                {additionalScripts}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Base CSS styles for all chart pages.
    /// </summary>
    public static string GetBaseStyles() => """
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }
        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, sans-serif;
            background: #1a1a2e;
            color: #eee;
            min-height: 100vh;
            padding: 20px;
        }
        .container {
            max-width: 1400px;
            margin: 0 auto;
        }
        h1 {
            text-align: center;
            margin-bottom: 10px;
            color: #fff;
            font-size: 1.8em;
        }
        .subtitle {
            text-align: center;
            color: #888;
            margin-bottom: 30px;
            font-size: 0.9em;
        }
        .chart-container {
            background: #16213e;
            border-radius: 12px;
            padding: 20px;
            margin-bottom: 20px;
            box-shadow: 0 4px 6px rgba(0, 0, 0, 0.3);
        }
        .chart-title {
            color: #fff;
            margin-bottom: 15px;
            font-size: 1.2em;
        }
        .stats-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
            gap: 15px;
            margin-bottom: 30px;
        }
        .stat-card {
            background: #16213e;
            border-radius: 8px;
            padding: 15px;
            text-align: center;
        }
        .stat-value {
            font-size: 2em;
            font-weight: bold;
            color: #0f9;
        }
        .stat-value.warning { color: #fa0; }
        .stat-value.error { color: #f55; }
        .stat-label {
            color: #888;
            font-size: 0.85em;
            margin-top: 5px;
        }
        .legend {
            display: flex;
            flex-wrap: wrap;
            gap: 15px;
            justify-content: center;
            margin-top: 15px;
        }
        .legend-item {
            display: flex;
            align-items: center;
            gap: 6px;
            font-size: 0.85em;
        }
        .legend-color {
            width: 12px;
            height: 12px;
            border-radius: 2px;
        }
        table {
            width: 100%;
            border-collapse: collapse;
            margin-top: 15px;
        }
        th, td {
            padding: 10px 12px;
            text-align: left;
            border-bottom: 1px solid #2a2a4a;
        }
        th {
            background: #1a1a3e;
            color: #888;
            font-weight: 500;
            font-size: 0.85em;
            text-transform: uppercase;
        }
        tr:hover {
            background: #1e1e3e;
        }
        .duration {
            font-family: 'Consolas', 'Monaco', monospace;
            color: #0f9;
        }
        .tag {
            display: inline-block;
            padding: 2px 8px;
            border-radius: 4px;
            font-size: 0.8em;
            font-weight: 500;
        }
        .tag-success { background: #0a3; color: #fff; }
        .tag-failed { background: #a33; color: #fff; }
        .tag-skipped { background: #555; color: #fff; }
        .tag-slower { background: #a33; color: #fff; }
        .tag-faster { background: #0a3; color: #fff; }
        .footer {
            text-align: center;
            color: #555;
            font-size: 0.8em;
            margin-top: 30px;
            padding-top: 20px;
            border-top: 1px solid #2a2a4a;
        }
        """;

    /// <summary>
    /// Escapes HTML special characters.
    /// </summary>
    public static string EscapeHtml(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    /// <summary>
    /// Formats a duration in milliseconds to a human-readable string.
    /// </summary>
    public static string FormatDuration(double milliseconds)
    {
        if (milliseconds < 1000)
            return $"{milliseconds:F0}ms";
        if (milliseconds < 60000)
            return $"{milliseconds / 1000:F1}s";
        if (milliseconds < 3600000)
            return $"{milliseconds / 60000:F1}m";
        return $"{milliseconds / 3600000:F1}h";
    }

    /// <summary>
    /// Gets a color from a predefined palette based on index.
    /// </summary>
    public static string GetColor(int index)
    {
        var colors = new[]
        {
            "#4dc9f6", "#f67019", "#f53794", "#537bc4", "#acc236",
            "#166a8f", "#00a950", "#58595b", "#8549ba", "#ff6384"
        };
        return colors[index % colors.Length];
    }

    /// <summary>
    /// Gets a color based on category.
    /// </summary>
    public static string GetCategoryColor(string category) => category.ToLowerInvariant() switch
    {
        "project" => "#4dc9f6",
        "target" => "#f67019",
        "task" => "#acc236",
        "compiler" => "#f53794",
        "copy" => "#537bc4",
        "restore" => "#166a8f",
        _ => "#58595b"
    };

    /// <summary>
    /// Gets a color based on status.
    /// </summary>
    public static string GetStatusColor(string? status) => status?.ToLowerInvariant() switch
    {
        "success" or "succeeded" => "#0a3",
        "failed" or "failure" => "#a33",
        "skipped" => "#555",
        _ => "#4dc9f6"
    };

    /// <summary>
    /// Generates a stat card HTML element.
    /// </summary>
    public static string StatCard(string label, string value, string? cssClass = null)
    {
        var valueClass = cssClass != null ? $"stat-value {cssClass}" : "stat-value";
        return $"""
            <div class="stat-card">
                <div class="{valueClass}">{EscapeHtml(value)}</div>
                <div class="stat-label">{EscapeHtml(label)}</div>
            </div>
            """;
    }

    /// <summary>
    /// Generates a legend item HTML element.
    /// </summary>
    public static string LegendItem(string label, string color)
    {
        return $"""
            <div class="legend-item">
                <div class="legend-color" style="background: {color}"></div>
                <span>{EscapeHtml(label)}</span>
            </div>
            """;
    }
}
