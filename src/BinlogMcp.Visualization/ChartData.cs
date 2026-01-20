namespace BinlogMcp.Visualization;

/// <summary>
/// Data for a timeline/Gantt chart showing parallel execution.
/// </summary>
public record TimelineData
{
    public required string Title { get; init; }
    public required DateTime BuildStart { get; init; }
    public required DateTime BuildEnd { get; init; }
    public required IReadOnlyList<TimelineRow> Rows { get; init; }
}

public record TimelineRow
{
    public required string Label { get; init; }
    public required string Category { get; init; } // "project", "target", "task"
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public string? Parent { get; init; }
    public string? Status { get; init; } // "success", "failed", "skipped"
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Data for a bar chart showing ranked items.
/// </summary>
public record BarChartData
{
    public required string Title { get; init; }
    public required string XAxisLabel { get; init; }
    public required string YAxisLabel { get; init; }
    public required IReadOnlyList<BarChartItem> Items { get; init; }
    public BarChartComparison? Comparison { get; init; }
}

public record BarChartItem
{
    public required string Label { get; init; }
    public required double Value { get; init; }
    public string? Category { get; init; }
    public string? Color { get; init; }
}

public record BarChartComparison
{
    public required string BaselineLabel { get; init; }
    public required string CurrentLabel { get; init; }
    public required IReadOnlyList<ComparisonItem> Items { get; init; }
}

public record ComparisonItem
{
    public required string Label { get; init; }
    public required double BaselineValue { get; init; }
    public required double CurrentValue { get; init; }
    public double Delta => CurrentValue - BaselineValue;
    public double DeltaPercent => BaselineValue > 0 ? (Delta / BaselineValue) * 100 : 0;
}

/// <summary>
/// Result of rendering a chart to HTML.
/// </summary>
public record ChartResult
{
    public required string FilePath { get; init; }
    public required string Html { get; init; }
}
