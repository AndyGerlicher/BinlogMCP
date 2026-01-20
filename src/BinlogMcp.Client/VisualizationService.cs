using System.Text.Json.Nodes;
using BinlogMcp.Visualization;

namespace BinlogMcp.Client;

/// <summary>
/// Service for generating visualizations from binlog data.
/// Extracted from Program.cs for testability.
/// </summary>
public class VisualizationService
{
    private readonly IMcpToolCaller _toolCaller;

    public VisualizationService(IMcpToolCaller toolCaller)
    {
        _toolCaller = toolCaller;
    }

    /// <summary>
    /// Generates timeline visualization data.
    /// </summary>
    public async Task<TimelineData> GetTimelineDataAsync(string binlogPath, string level = "targets")
    {
        var args = new JsonObject { ["binlogPath"] = binlogPath, ["level"] = level };
        var json = await _toolCaller.CallToolAsync("GetTimeline", args);

        return ChartRenderer.ParseTimelineJson(json, $"Build Timeline - {Path.GetFileName(binlogPath)}");
    }

    /// <summary>
    /// Generates performance bar chart data (slowest targets).
    /// </summary>
    public async Task<BarChartData> GetPerformanceDataAsync(string binlogPath, int top = 30)
    {
        var args = new JsonObject { ["binlogPath"] = binlogPath, ["top"] = top };
        var json = await _toolCaller.CallToolAsync("GetTargets", args);

        return ChartRenderer.ParsePerformanceJson(json, $"Slowest Targets - {Path.GetFileName(binlogPath)}", "targets");
    }

    /// <summary>
    /// Generates comparison bar chart data between two builds.
    /// </summary>
    public async Task<BarChartData> GetComparisonDataAsync(string baselinePath, string currentPath, int top = 30)
    {
        var baselineArgs = new JsonObject { ["binlogPath"] = baselinePath, ["top"] = top };
        var currentArgs = new JsonObject { ["binlogPath"] = currentPath, ["top"] = top };

        var baselineJson = await _toolCaller.CallToolAsync("GetTargets", baselineArgs);
        var currentJson = await _toolCaller.CallToolAsync("GetTargets", currentArgs);

        return ChartRenderer.ParseComparisonJson(
            baselineJson,
            currentJson,
            Path.GetFileName(baselinePath),
            Path.GetFileName(currentPath),
            "Build Performance Comparison",
            "targets");
    }

    /// <summary>
    /// Renders timeline and opens in browser.
    /// </summary>
    public async Task<ChartResult> RenderTimelineAsync(string binlogPath, string? outputPath = null)
    {
        var data = await GetTimelineDataAsync(binlogPath);
        return ChartRenderer.RenderTimeline(data, outputPath);
    }

    /// <summary>
    /// Renders performance chart and opens in browser.
    /// </summary>
    public async Task<ChartResult> RenderPerformanceAsync(string binlogPath, string? outputPath = null)
    {
        var data = await GetPerformanceDataAsync(binlogPath);
        return ChartRenderer.RenderBarChart(data, outputPath);
    }

    /// <summary>
    /// Renders comparison chart and opens in browser.
    /// </summary>
    public async Task<ChartResult> RenderComparisonAsync(string baselinePath, string currentPath, string? outputPath = null)
    {
        var data = await GetComparisonDataAsync(baselinePath, currentPath);
        return ChartRenderer.RenderBarChart(data, outputPath);
    }
}

/// <summary>
/// Interface for calling MCP tools. Allows mocking in tests.
/// </summary>
public interface IMcpToolCaller
{
    Task<string> CallToolAsync(string toolName, JsonObject arguments);
}

/// <summary>
/// Adapter that wraps McpClient to implement IMcpToolCaller.
/// </summary>
public class McpClientAdapter : IMcpToolCaller
{
    private readonly McpClient _client;

    public McpClientAdapter(McpClient client)
    {
        _client = client;
    }

    public Task<string> CallToolAsync(string toolName, JsonObject arguments)
    {
        return _client.CallToolAsync(toolName, arguments);
    }
}
