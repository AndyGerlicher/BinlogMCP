using BinlogMcp.Formatting;
using Xunit;

namespace BinlogMcp.Tests;

public class MarkdownFormatterTests
{
    [Fact]
    public void Format_SimpleObject_ReturnsBulletList()
    {
        var data = new { name = "Test", value = 42, succeeded = true };
        var result = MarkdownFormatter.Format(data, new FormatOptions { Title = "Test Object" });

        Assert.Contains("# Test Object", result);
        Assert.Contains("- **Name**: Test", result);
        Assert.Contains("- **Value**: 42", result);
        Assert.Contains("- **Succeeded**: Yes", result);
    }

    [Fact]
    public void Format_ObjectWithArray_ReturnsTable()
    {
        var data = new
        {
            count = 2,
            items = new[]
            {
                new { name = "Item1", value = 10 },
                new { name = "Item2", value = 20 }
            }
        };

        var result = MarkdownFormatter.Format(data);

        Assert.Contains("| Name | Value |", result);
        Assert.Contains("| --- | --- |", result);
        Assert.Contains("| Item1 | 10 |", result);
        Assert.Contains("| Item2 | 20 |", result);
    }

    [Fact]
    public void Format_EmptyArray_ShowsNoItems()
    {
        var data = new { items = Array.Empty<object>() };
        var result = MarkdownFormatter.Format(data);

        Assert.Contains("*No items*", result);
    }

    [Fact]
    public void Format_BooleanValues_ShowsYesNo()
    {
        var data = new { enabled = true, disabled = false };
        var result = MarkdownFormatter.Format(data);

        Assert.Contains("Yes", result);
        Assert.Contains("No", result);
    }

    [Fact]
    public void Format_NullValue_ShowsNull()
    {
        var data = new { value = (string?)null };
        var result = MarkdownFormatter.Format(data);

        Assert.Contains("*null*", result);
    }

    [Fact]
    public void FormatPropertyName_CamelCase_ConvertsTitleCase()
    {
        Assert.Equal("Project Name", MarkdownFormatter.FormatPropertyName("projectName"));
        Assert.Equal("Duration Ms", MarkdownFormatter.FormatPropertyName("durationMs"));
        Assert.Equal("Id", MarkdownFormatter.FormatPropertyName("id"));
    }

    [Fact]
    public void Format_NestedObject_ShowsAsSubsection()
    {
        var data = new
        {
            name = "Test",
            details = new { inner = "value", count = 5 }
        };

        var result = MarkdownFormatter.Format(data);

        Assert.Contains("## Details", result);
        Assert.Contains("- **Inner**: value", result);
    }

    [Fact]
    public void Format_DurationMilliseconds_FormatsAsSeconds()
    {
        var data = new { durationMs = 2500.5 };
        var result = MarkdownFormatter.Format(data);

        Assert.Contains("2.5s", result);
    }

    [Fact]
    public void Format_TableCellWithPipe_EscapesPipe()
    {
        var data = new
        {
            items = new[]
            {
                new { path = "a|b", name = "test" }
            }
        };

        var result = MarkdownFormatter.Format(data);

        // When formatted as table, pipes should be escaped
        Assert.Contains(@"a\|b", result);
    }

    [Fact]
    public void Format_BuildSummaryLike_IncludesAllParts()
    {
        var data = new
        {
            file = "test.binlog",
            succeeded = true,
            errorCount = 0,
            warningCount = 5,
            projects = new[]
            {
                new { name = "Project1", targetFramework = "net10.0" },
                new { name = "Project2", targetFramework = "net10.0" }
            }
        };

        var result = MarkdownFormatter.Format(data, new FormatOptions { Title = "Build Summary" });

        Assert.Contains("# Build Summary", result);
        Assert.Contains("- **File**: test.binlog", result);
        Assert.Contains("- **Succeeded**: Yes", result);
        Assert.Contains("| Project1 | net10.0 |", result);
    }
}
