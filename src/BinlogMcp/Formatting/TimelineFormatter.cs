using System.Text.Json;

namespace BinlogMcp.Formatting;

/// <summary>
/// Formats timing data as timeline JSON for visualization.
/// </summary>
public static class TimelineFormatter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Formats data as timeline JSON with events that have timing information.
    /// </summary>
    public static string Format(object data, FormatOptions? options = null)
    {
        options ??= FormatOptions.Default;

        // Convert to JsonElement for uniform handling
        var json = JsonSerializer.SerializeToElement(data);

        var events = new List<TimelineEvent>();
        ExtractEvents(json, events, null);

        if (events.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                title = options.Title ?? "Timeline",
                generated = DateTime.UtcNow.ToString("o"),
                message = "No timing events found",
                events = Array.Empty<object>()
            }, JsonOptions);
        }

        // Sort by start time
        events.Sort((a, b) => a.Start.CompareTo(b.Start));

        var result = new
        {
            title = options.Title ?? "Timeline",
            generated = DateTime.UtcNow.ToString("o"),
            eventCount = events.Count,
            events = events.Select(e => new
            {
                id = e.Id,
                label = e.Label,
                category = e.Category,
                start = e.Start.ToString("o"),
                end = e.End.ToString("o"),
                durationMs = (e.End - e.Start).TotalMilliseconds,
                succeeded = e.Succeeded,
                metadata = e.Metadata
            })
        };

        return JsonSerializer.Serialize(result, JsonOptions);
    }

    private static void ExtractEvents(JsonElement element, List<TimelineEvent> events, string? category)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractEvents(item, events, category);
            }
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        // Try to extract timing from this object
        var timing = TryExtractTiming(element);
        if (timing != null)
        {
            events.Add(timing);
        }

        // Check known array properties for nested events
        foreach (var prop in element.EnumerateObject())
        {
            if (FormatOptions.CommonArrayPropertyNames.Contains(prop.Name.ToLowerInvariant()) && prop.Value.ValueKind == JsonValueKind.Array)
            {
                ExtractEvents(prop.Value, events, prop.Name.TrimEnd('s'));
            }
        }
    }

    private static TimelineEvent? TryExtractTiming(JsonElement element)
    {
        // Look for start/end time fields
        DateTime? startTime = null;
        DateTime? endTime = null;
        string? label = null;
        string? id = null;
        bool? succeeded = null;
        string? category = null;
        var metadata = new Dictionary<string, object?>();

        foreach (var prop in element.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();

            switch (name)
            {
                case "starttime" or "start":
                    startTime = TryParseDateTime(prop.Value);
                    break;
                case "endtime" or "end":
                    endTime = TryParseDateTime(prop.Value);
                    break;
                case "name" or "label" or "title":
                    label = prop.Value.GetString();
                    break;
                case "id":
                    id = prop.Value.ToString();
                    break;
                case "succeeded" or "success":
                    succeeded = prop.Value.ValueKind == JsonValueKind.True;
                    break;
                case "category" or "type":
                    category = prop.Value.GetString();
                    break;
                case "project":
                    metadata["project"] = prop.Value.GetString();
                    break;
                case "durationms" or "duration":
                    // If we have duration but no times, we can't create timeline event
                    // but capture it as metadata
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                        metadata["durationMs"] = prop.Value.GetDouble();
                    break;
                default:
                    // Capture other simple properties as metadata
                    if (IsSimpleValue(prop.Value) && !name.Contains("time"))
                    {
                        metadata[prop.Name] = GetSimpleValue(prop.Value);
                    }
                    break;
            }
        }

        // Must have both start and end times
        if (startTime == null || endTime == null)
            return null;

        // Generate ID if not provided
        id ??= $"{category ?? "event"}:{label ?? Guid.NewGuid().ToString("N")[..8]}";

        return new TimelineEvent
        {
            Id = id,
            Label = label ?? "Unknown",
            Category = category ?? "event",
            Start = startTime.Value,
            End = endTime.Value,
            Succeeded = succeeded,
            Metadata = metadata.Count > 0 ? metadata : null
        };
    }

    private static DateTime? TryParseDateTime(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(element.GetString(), out var dt))
                return dt;
        }
        return null;
    }

    private static bool IsSimpleValue(JsonElement element) => FormatterHelpers.IsSimpleValue(element);

    private static object? GetSimpleValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private class TimelineEvent
    {
        public required string Id { get; set; }
        public required string Label { get; set; }
        public required string Category { get; set; }
        public required DateTime Start { get; set; }
        public required DateTime End { get; set; }
        public bool? Succeeded { get; set; }
        public Dictionary<string, object?>? Metadata { get; set; }
    }
}
