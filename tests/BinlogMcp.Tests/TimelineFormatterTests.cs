using BinlogMcp.Formatting;
using System.Text.Json;
using Xunit;

namespace BinlogMcp.Tests;

public class TimelineFormatterTests
{
    [Fact]
    public void Format_ObjectsWithTiming_ExtractsEvents()
    {
        var startTime = new DateTime(2024, 1, 1, 10, 0, 0);
        var endTime = new DateTime(2024, 1, 1, 10, 0, 1);

        var data = new
        {
            targets = new[]
            {
                new { name = "Build", startTime = startTime.ToString("o"), endTime = endTime.ToString("o"), succeeded = true }
            }
        };

        var result = TimelineFormatter.Format(data, new FormatOptions { Title = "Build Timeline" });
        var json = JsonDocument.Parse(result);

        Assert.Equal("Build Timeline", json.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("eventCount").GetInt32());

        var events = json.RootElement.GetProperty("events");
        Assert.Equal(1, events.GetArrayLength());

        var firstEvent = events[0];
        Assert.Equal("Build", firstEvent.GetProperty("label").GetString());
        Assert.Equal(1000, firstEvent.GetProperty("durationMs").GetDouble());
    }

    [Fact]
    public void Format_NoTimingData_ReturnsEmptyEvents()
    {
        var data = new
        {
            items = new[]
            {
                new { name = "Item1", value = 10 }
            }
        };

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);

        Assert.Contains("No timing events found", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public void Format_MultipleEvents_SortsByStartTime()
    {
        var baseTime = new DateTime(2024, 1, 1, 10, 0, 0);

        var data = new
        {
            targets = new[]
            {
                new { name = "Second", startTime = baseTime.AddSeconds(1).ToString("o"), endTime = baseTime.AddSeconds(2).ToString("o") },
                new { name = "First", startTime = baseTime.ToString("o"), endTime = baseTime.AddSeconds(1).ToString("o") }
            }
        };

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);
        var events = json.RootElement.GetProperty("events");

        Assert.Equal("First", events[0].GetProperty("label").GetString());
        Assert.Equal("Second", events[1].GetProperty("label").GetString());
    }

    [Fact]
    public void Format_EventWithSucceeded_IncludesSucceeded()
    {
        var startTime = new DateTime(2024, 1, 1, 10, 0, 0);
        var endTime = new DateTime(2024, 1, 1, 10, 0, 1);

        var data = new
        {
            targets = new[]
            {
                new { name = "Compile", startTime = startTime.ToString("o"), endTime = endTime.ToString("o"), succeeded = true }
            }
        };

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);
        var events = json.RootElement.GetProperty("events");

        Assert.True(events[0].GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public void Format_EventWithMetadata_IncludesMetadata()
    {
        var startTime = new DateTime(2024, 1, 1, 10, 0, 0);
        var endTime = new DateTime(2024, 1, 1, 10, 0, 1);

        var data = new
        {
            targets = new[]
            {
                new { name = "Build", project = "MyProject", startTime = startTime.ToString("o"), endTime = endTime.ToString("o") }
            }
        };

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);
        var events = json.RootElement.GetProperty("events");

        var metadata = events[0].GetProperty("metadata");
        Assert.Equal("MyProject", metadata.GetProperty("project").GetString());
    }

    [Fact]
    public void Format_IncludesGeneratedTimestamp()
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

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);

        var generated = json.RootElement.GetProperty("generated").GetString();
        Assert.NotNull(generated);
        Assert.True(DateTime.TryParse(generated, out _));
    }

    [Fact]
    public void Format_CalculatesDurationCorrectly()
    {
        var startTime = new DateTime(2024, 1, 1, 10, 0, 0);
        var endTime = new DateTime(2024, 1, 1, 10, 0, 2, 500); // 2.5 seconds

        var data = new
        {
            targets = new[]
            {
                new { name = "Build", startTime = startTime.ToString("o"), endTime = endTime.ToString("o") }
            }
        };

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);
        var events = json.RootElement.GetProperty("events");

        Assert.Equal(2500, events[0].GetProperty("durationMs").GetDouble());
    }

    [Fact]
    public void Format_GeneratesEventId()
    {
        var startTime = new DateTime(2024, 1, 1, 10, 0, 0);
        var endTime = new DateTime(2024, 1, 1, 10, 0, 1);

        var data = new
        {
            targets = new[]
            {
                new { name = "Build", startTime = startTime.ToString("o"), endTime = endTime.ToString("o") }
            }
        };

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);
        var events = json.RootElement.GetProperty("events");

        var id = events[0].GetProperty("id").GetString();
        Assert.NotNull(id);
        Assert.Contains("Build", id);
    }

    [Fact]
    public void Format_EventWithCategory_SetsCategory()
    {
        var startTime = new DateTime(2024, 1, 1, 10, 0, 0);
        var endTime = new DateTime(2024, 1, 1, 10, 0, 1);

        var data = new
        {
            targets = new[]
            {
                new { name = "Build", category = "compile", startTime = startTime.ToString("o"), endTime = endTime.ToString("o") }
            }
        };

        var result = TimelineFormatter.Format(data);
        var json = JsonDocument.Parse(result);
        var events = json.RootElement.GetProperty("events");

        Assert.Equal("compile", events[0].GetProperty("category").GetString());
    }
}
