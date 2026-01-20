using System.Text.Json;
using System.Text.Json.Nodes;

namespace BinlogMcp.Client;

/// <summary>
/// Service for comparing builds using MCP tools.
/// Extracted from Program.cs for testability.
/// </summary>
public class BuildComparisonService
{
    private readonly IMcpToolCaller _toolCaller;

    public BuildComparisonService(IMcpToolCaller toolCaller)
    {
        _toolCaller = toolCaller;
    }

    /// <summary>
    /// Compares two builds and returns a structured result.
    /// </summary>
    public async Task<BuildComparisonResult> CompareBuildsAsync(string baselinePath, string currentPath)
    {
        var args = new JsonObject
        {
            ["baselinePath"] = baselinePath,
            ["comparisonPath"] = currentPath
        };

        var json = await _toolCaller.CallToolAsync("CompareBinlogs", args);
        return ParseComparisonResult(json);
    }

    /// <summary>
    /// Gets incremental build analysis for a single build.
    /// </summary>
    public async Task<IncrementalAnalysisResult> GetIncrementalAnalysisAsync(string binlogPath)
    {
        var args = new JsonObject { ["binlogPath"] = binlogPath };
        var json = await _toolCaller.CallToolAsync("GetIncrementalBuildAnalysis", args);
        return ParseIncrementalAnalysis(json);
    }

    private static BuildComparisonResult ParseComparisonResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new BuildComparisonResult();

        if (root.TryGetProperty("summary", out var summary))
        {
            if (summary.TryGetProperty("baseline", out var baseline))
            {
                result.BaselineDurationMs = baseline.GetProperty("durationMs").GetDouble();
                result.BaselineSucceeded = baseline.GetProperty("succeeded").GetBoolean();
                result.BaselineErrorCount = baseline.GetProperty("errorCount").GetInt32();
            }

            if (summary.TryGetProperty("comparison", out var comparison))
            {
                result.ComparisonDurationMs = comparison.GetProperty("durationMs").GetDouble();
                result.ComparisonSucceeded = comparison.GetProperty("succeeded").GetBoolean();
                result.ComparisonErrorCount = comparison.GetProperty("errorCount").GetInt32();
            }
        }

        if (root.TryGetProperty("buildTypeAnalysis", out var analysis))
        {
            if (analysis.TryGetProperty("baseline", out var baselineAnalysis))
            {
                result.BaselineIsLikelyClean = baselineAnalysis.GetProperty("isLikelyCleanBuild").GetBoolean();
                result.BaselineIsLikelyIncremental = baselineAnalysis.GetProperty("isLikelyIncrementalBuild").GetBoolean();
                result.BaselineSkippedPercent = baselineAnalysis.GetProperty("skippedPercent").GetDouble();
            }

            if (analysis.TryGetProperty("comparison", out var comparisonAnalysis))
            {
                result.ComparisonIsLikelyClean = comparisonAnalysis.GetProperty("isLikelyCleanBuild").GetBoolean();
                result.ComparisonIsLikelyIncremental = comparisonAnalysis.GetProperty("isLikelyIncrementalBuild").GetBoolean();
                result.ComparisonSkippedPercent = comparisonAnalysis.GetProperty("skippedPercent").GetDouble();
            }
        }

        if (root.TryGetProperty("conclusion", out var conclusion))
        {
            result.Conclusion = conclusion.GetString() ?? "";
        }

        if (root.TryGetProperty("errors", out var errors))
        {
            result.NewErrorCount = errors.GetProperty("newCount").GetInt32();
            result.FixedErrorCount = errors.GetProperty("fixedCount").GetInt32();
        }

        return result;
    }

    private static IncrementalAnalysisResult ParseIncrementalAnalysis(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new IncrementalAnalysisResult();

        if (root.TryGetProperty("summary", out var summary))
        {
            result.TotalTargets = summary.GetProperty("totalTargets").GetInt32();
            result.ExecutedTargets = summary.GetProperty("executedTargets").GetInt32();
            result.SkippedTargets = summary.GetProperty("skippedTargets").GetInt32();
            result.IncrementalEfficiencyPercent = summary.GetProperty("incrementalEfficiencyPercent").GetDouble();
            result.TotalExecutionTimeMs = summary.GetProperty("totalExecutionTimeMs").GetDouble();
        }

        return result;
    }
}

/// <summary>
/// Result of comparing two builds.
/// </summary>
public class BuildComparisonResult
{
    // Baseline metrics
    public double BaselineDurationMs { get; set; }
    public bool BaselineSucceeded { get; set; }
    public int BaselineErrorCount { get; set; }
    public bool BaselineIsLikelyClean { get; set; }
    public bool BaselineIsLikelyIncremental { get; set; }
    public double BaselineSkippedPercent { get; set; }

    // Comparison metrics
    public double ComparisonDurationMs { get; set; }
    public bool ComparisonSucceeded { get; set; }
    public int ComparisonErrorCount { get; set; }
    public bool ComparisonIsLikelyClean { get; set; }
    public bool ComparisonIsLikelyIncremental { get; set; }
    public double ComparisonSkippedPercent { get; set; }

    // Diff metrics
    public int NewErrorCount { get; set; }
    public int FixedErrorCount { get; set; }

    // Analysis
    public string Conclusion { get; set; } = "";

    /// <summary>
    /// Duration change as a percentage (negative = faster).
    /// </summary>
    public double DurationChangePercent => BaselineDurationMs > 0
        ? ((ComparisonDurationMs - BaselineDurationMs) / BaselineDurationMs) * 100
        : 0;

    /// <summary>
    /// Whether this comparison suggests incremental vs clean builds.
    /// </summary>
    public bool IsLikelyIncrementalComparison =>
        Conclusion.Contains("INCREMENTAL", StringComparison.OrdinalIgnoreCase) ||
        (DurationChangePercent < -40 && BaselineIsLikelyClean);
}

/// <summary>
/// Result of incremental build analysis.
/// </summary>
public class IncrementalAnalysisResult
{
    public int TotalTargets { get; set; }
    public int ExecutedTargets { get; set; }
    public int SkippedTargets { get; set; }
    public double IncrementalEfficiencyPercent { get; set; }
    public double TotalExecutionTimeMs { get; set; }

    /// <summary>
    /// Whether this build is likely an incremental build.
    /// </summary>
    public bool IsLikelyIncremental => IncrementalEfficiencyPercent > 60;
}
