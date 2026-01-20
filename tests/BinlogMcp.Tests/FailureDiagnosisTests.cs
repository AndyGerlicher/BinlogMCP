using System.Text.Json;
using BinlogMcp.Tools;
using Xunit;

namespace BinlogMcp.Tests;

public class FailureDiagnosisTests
{
    [Fact]
    public void GetFailureDiagnosis_FileNotFound_ReturnsError()
    {
        var result = BinlogTools.GetFailureDiagnosis("/nonexistent/file.binlog");
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("error", out var error));
        Assert.Contains("not found", error.GetString());
    }

    [Fact]
    public void GetFailureDiagnosis_SuccessfulBuild_ReturnsSuccessMessage()
    {
        var binlogPath = FindTestBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetFailureDiagnosis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("succeeded", out var succeeded));
        Assert.True(succeeded.GetBoolean());

        Assert.True(json.RootElement.TryGetProperty("message", out var message));
        Assert.Contains("succeeded", message.GetString());
    }

    [Fact]
    public void GetFailureDiagnosis_FailedBuild_ReturnsDiagnosis()
    {
        var binlogPath = FindErrorBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetFailureDiagnosis(binlogPath);
        var json = JsonDocument.Parse(result);

        Assert.True(json.RootElement.TryGetProperty("succeeded", out var succeeded));
        Assert.False(succeeded.GetBoolean());

        Assert.True(json.RootElement.TryGetProperty("summary", out var summary));
        Assert.True(summary.TryGetProperty("totalErrors", out var totalErrors));
        Assert.True(totalErrors.GetInt32() > 0);

        Assert.True(json.RootElement.TryGetProperty("diagnoses", out var diagnoses));
        Assert.True(diagnoses.GetArrayLength() > 0);

        Console.WriteLine(result);
    }

    [Fact]
    public void GetFailureDiagnosis_ReturnsDiagnosesWithSuggestions()
    {
        var binlogPath = FindErrorBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetFailureDiagnosis(binlogPath);
        var json = JsonDocument.Parse(result);

        var diagnoses = json.RootElement.GetProperty("diagnoses");
        Assert.True(diagnoses.GetArrayLength() > 0);

        var firstDiagnosis = diagnoses[0];
        Assert.True(firstDiagnosis.TryGetProperty("category", out _));
        Assert.True(firstDiagnosis.TryGetProperty("description", out _));
        Assert.True(firstDiagnosis.TryGetProperty("suggestions", out var suggestions));
        Assert.True(suggestions.GetArrayLength() > 0);
    }

    [Fact]
    public void GetFailureDiagnosis_DiagnosesHavePriority()
    {
        var binlogPath = FindErrorBinlog();
        if (binlogPath == null)
            return;

        var result = BinlogTools.GetFailureDiagnosis(binlogPath);
        var json = JsonDocument.Parse(result);

        var diagnoses = json.RootElement.GetProperty("diagnoses");
        if (diagnoses.GetArrayLength() > 1)
        {
            // Diagnoses should be sorted by priority (highest first)
            var firstPriority = diagnoses[0].GetProperty("priority").GetInt32();
            var secondPriority = diagnoses[1].GetProperty("priority").GetInt32();
            Assert.True(firstPriority >= secondPriority);
        }
    }

    private static string? FindTestBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test.binlog");
            if (File.Exists(binlog))
                return binlog;
            dir = dir.Parent;
        }
        return null;
    }

    private static string? FindErrorBinlog()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test-data", "error.binlog");
            if (File.Exists(binlog))
                return binlog;
            dir = dir.Parent;
        }
        return null;
    }
}
