using BinlogMcp.Formatting;
using Xunit;

namespace BinlogMcp.Tests;

public class CsvFormatterTests
{
    [Fact]
    public void Format_ObjectWithArray_ExtractsArrayToCsv()
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

        var result = CsvFormatter.Format(data);

        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Equal("name,value", lines[0]);
        Assert.Equal("Item1,10", lines[1]);
        Assert.Equal("Item2,20", lines[2]);
    }

    [Fact]
    public void Format_NoArray_ReturnsNoDataMessage()
    {
        var data = new { name = "Test", value = 42 };
        var result = CsvFormatter.Format(data);

        Assert.Equal("# No tabular data found", result);
    }

    [Fact]
    public void Format_EmptyArray_ReturnsNoDataMessage()
    {
        var data = new { items = Array.Empty<object>() };
        var result = CsvFormatter.Format(data);

        Assert.Equal("# No tabular data found", result);
    }

    [Fact]
    public void Format_FieldWithComma_QuotesField()
    {
        var data = new
        {
            items = new[]
            {
                new { message = "Hello, World", code = "CS0001" }
            }
        };

        var result = CsvFormatter.Format(data);

        Assert.Contains("\"Hello, World\"", result);
    }

    [Fact]
    public void Format_FieldWithQuotes_EscapesQuotes()
    {
        var data = new
        {
            items = new[]
            {
                new { message = "Say \"Hello\"", code = "CS0001" }
            }
        };

        var result = CsvFormatter.Format(data);

        Assert.Contains("\"Say \"\"Hello\"\"\"", result);
    }

    [Fact]
    public void Format_FieldWithNewline_QuotesField()
    {
        var data = new
        {
            items = new[]
            {
                new { message = "Line1\nLine2", code = "CS0001" }
            }
        };

        var result = CsvFormatter.Format(data);

        Assert.Contains("\"Line1\nLine2\"", result);
    }

    [Fact]
    public void Format_WithCustomDelimiter_UsesDelimiter()
    {
        var data = new
        {
            items = new[]
            {
                new { name = "Test", value = 42 }
            }
        };

        var result = CsvFormatter.Format(data, new FormatOptions { CsvDelimiter = ';' });

        Assert.Contains("name;value", result);
        Assert.Contains("Test;42", result);
    }

    [Fact]
    public void Format_NestedObject_FlattensWithDotNotation()
    {
        var data = new
        {
            items = new[]
            {
                new { name = "Test", info = new { nested = "value" } }
            }
        };

        var result = CsvFormatter.Format(data);

        Assert.Contains("info.nested", result);
        Assert.Contains("value", result);
    }

    [Fact]
    public void Format_WithoutHeaders_OmitsHeaderRow()
    {
        var data = new
        {
            items = new[]
            {
                new { name = "Test", value = 42 }
            }
        };

        var result = CsvFormatter.Format(data, new FormatOptions { IncludeCsvHeaders = false });

        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal("Test,42", lines[0]);
    }

    [Fact]
    public void Format_ErrorsArray_FindsPriorityArray()
    {
        var data = new
        {
            file = "test.binlog",
            count = 1,
            errors = new[]
            {
                new { code = "CS0001", message = "Error message" }
            }
        };

        var result = CsvFormatter.Format(data);

        Assert.Contains("code,message", result);
        Assert.Contains("CS0001,Error message", result);
    }

    [Fact]
    public void Format_BooleanValues_FormatsAsLowercase()
    {
        var data = new
        {
            items = new[]
            {
                new { name = "Test", succeeded = true, failed = false }
            }
        };

        var result = CsvFormatter.Format(data);

        Assert.Contains("true", result);
        Assert.Contains("false", result);
    }

    [Fact]
    public void Format_NullValue_ShowsEmptyString()
    {
        var data = new
        {
            items = new[]
            {
                new { name = "Test", value = (string?)null }
            }
        };

        var result = CsvFormatter.Format(data);
        var lines = result.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Test,", lines[1]);
    }
}
