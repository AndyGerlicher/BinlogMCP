using BinlogMcp.Formatting;
using Xunit;

namespace BinlogMcp.Tests;

public class OutputFormatterTests
{
    [Theory]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("JSON", OutputFormat.Json)]
    [InlineData("markdown", OutputFormat.Markdown)]
    [InlineData("md", OutputFormat.Markdown)]
    [InlineData("csv", OutputFormat.Csv)]
    [InlineData("CSV", OutputFormat.Csv)]
    [InlineData("timeline", OutputFormat.Timeline)]
    [InlineData("", OutputFormat.Json)]
    [InlineData(null, OutputFormat.Json)]
    public void TryParseFormat_ValidFormats_ReturnsTrue(string? input, OutputFormat expected)
    {
        var result = OutputFormatter.TryParseFormat(input, out var format);

        Assert.True(result);
        Assert.Equal(expected, format);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("xml")]
    [InlineData("yaml")]
    public void TryParseFormat_InvalidFormats_ReturnsFalse(string input)
    {
        var result = OutputFormatter.TryParseFormat(input, out _);

        Assert.False(result);
    }

    [Fact]
    public void Format_JsonFormat_ReturnsJson()
    {
        var data = new { name = "Test", value = 42 };
        var result = OutputFormatter.Format(data, OutputFormat.Json);

        Assert.Contains("\"name\": \"Test\"", result);
        Assert.Contains("\"value\": 42", result);
    }

    [Fact]
    public void Format_MarkdownFormat_ReturnsMarkdown()
    {
        var data = new { name = "Test", value = 42 };
        var result = OutputFormatter.Format(data, OutputFormat.Markdown, "Test Title");

        Assert.Contains("# Test Title", result);
        Assert.Contains("- **Name**: Test", result);
    }

    [Fact]
    public void Format_CsvFormat_ReturnsCsv()
    {
        var data = new
        {
            items = new[]
            {
                new { name = "Test", value = 42 }
            }
        };
        var result = OutputFormatter.Format(data, OutputFormat.Csv);

        Assert.Contains("name,value", result);
        Assert.Contains("Test,42", result);
    }

    [Fact]
    public void Format_TimelineFormat_ReturnsTimelineJson()
    {
        var startTime = DateTime.UtcNow;
        var endTime = startTime.AddSeconds(1);

        var data = new
        {
            targets = new[]
            {
                new { name = "Build", startTime = startTime.ToString("o"), endTime = endTime.ToString("o") }
            }
        };
        var result = OutputFormatter.Format(data, OutputFormat.Timeline, "Build Timeline");

        Assert.Contains("\"title\": \"Build Timeline\"", result);
        Assert.Contains("\"events\":", result);
    }

    [Fact]
    public void Format_WithTitle_PassesTitleToFormatter()
    {
        var data = new { name = "Test" };
        var result = OutputFormatter.Format(data, OutputFormat.Markdown, "Custom Title");

        Assert.Contains("# Custom Title", result);
    }

    [Fact]
    public void Format_DefaultFormat_ReturnsJson()
    {
        var data = new { name = "Test" };
        var result = OutputFormatter.Format(data, (OutputFormat)999);

        Assert.Contains("\"name\": \"Test\"", result);
    }
}
