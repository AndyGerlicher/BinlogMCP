using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BinlogMcp.Client;

/// <summary>
/// Simple MCP client that communicates with an MCP server over stdio using JSON-RPC 2.0.
/// Properly handles:
/// - Stderr draining (prevents server blocking on stderr buffer)
/// - JSON-RPC message routing (handles notifications and id correlation)
/// - Content type/isError handling
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pendingRequests = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoopTask;
    private readonly Task _stderrDrainTask;
    private int _requestId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private McpClient(Process process)
    {
        _process = process;
        _writer = process.StandardInput;

        // Start background tasks for reading stdout and draining stderr
        _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token));
        _stderrDrainTask = Task.Run(() => DrainStderrAsync(_cts.Token));
    }

    public static async Task<McpClient> StartAsync(string command, string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start MCP server process");

        var client = new McpClient(process);
        await client.InitializeAsync();
        return client;
    }

    /// <summary>
    /// Background task that continuously reads stderr to prevent buffer blocking.
    /// </summary>
    private async Task DrainStderrAsync(CancellationToken ct)
    {
        try
        {
            var reader = _process.StandardError;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break; // EOF

                // Optionally log stderr for debugging
                // Console.Error.WriteLine($"[MCP stderr] {line}");
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception)
        {
            // Ignore errors during stderr drain
        }
    }

    /// <summary>
    /// Background task that reads stdout and routes JSON-RPC messages.
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            var reader = _process.StandardOutput;
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break; // EOF

                try
                {
                    var message = JsonNode.Parse(line);
                    if (message == null) continue;

                    // Check if this is a response (has "id") or notification (no "id")
                    if (message["id"] is JsonValue idValue)
                    {
                        // This is a response to a request
                        var id = idValue.GetValue<int>();
                        if (_pendingRequests.TryRemove(id, out var tcs))
                        {
                            if (message["error"] is JsonObject error)
                            {
                                var errorMessage = error["message"]?.GetValue<string>() ?? "Unknown error";
                                tcs.SetException(new InvalidOperationException($"MCP error: {errorMessage}"));
                            }
                            else if (message["result"] is JsonNode result)
                            {
                                tcs.SetResult(result);
                            }
                            else
                            {
                                tcs.SetException(new InvalidOperationException("No result or error in response"));
                            }
                        }
                        // else: response for unknown request id, ignore
                    }
                    else
                    {
                        // This is a notification (no id) - ignore for now
                        // Could handle logging notifications, progress updates, etc.
                    }
                }
                catch (JsonException)
                {
                    // Invalid JSON, skip this line
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            // Fail all pending requests on read loop error
            foreach (var kvp in _pendingRequests)
            {
                if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
                {
                    tcs.TrySetException(ex);
                }
            }
        }
    }

    private async Task InitializeAsync()
    {
        // Send initialize request
        var initResult = await SendRequestAsync<JsonObject>("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new
            {
                name = "BinlogMcp.Client",
                version = "1.0.0"
            }
        });

        // Send initialized notification
        await SendNotificationAsync("notifications/initialized", null);
    }

    public async Task<List<McpTool>> ListToolsAsync()
    {
        var result = await SendRequestAsync<JsonObject>("tools/list", new { });

        var tools = new List<McpTool>();
        if (result.TryGetPropertyValue("tools", out var toolsNode) && toolsNode is JsonArray toolsArray)
        {
            foreach (var toolNode in toolsArray)
            {
                if (toolNode is JsonObject tool)
                {
                    var mcpTool = new McpTool
                    {
                        Name = tool["name"]?.GetValue<string>() ?? "",
                        Description = tool["description"]?.GetValue<string>(),
                        InputSchema = tool["inputSchema"]
                    };
                    tools.Add(mcpTool);
                }
            }
        }

        return tools;
    }

    public async Task<string> CallToolAsync(string name, JsonObject arguments)
    {
        var result = await SendRequestAsync<JsonObject>("tools/call", new
        {
            name,
            arguments
        });

        // Check for isError flag at the result level
        if (result.TryGetPropertyValue("isError", out var isErrorNode) &&
            isErrorNode is JsonValue isErrorValue &&
            isErrorValue.GetValue<bool>())
        {
            // Tool returned an error
            var errorContent = ExtractContentText(result);
            throw new InvalidOperationException($"Tool error: {errorContent}");
        }

        // Extract content from result
        return ExtractContentText(result);
    }

    private static string ExtractContentText(JsonObject result)
    {
        if (!result.TryGetPropertyValue("content", out var contentNode) || contentNode is not JsonArray contentArray)
        {
            return result.ToJsonString();
        }

        var sb = new StringBuilder();
        foreach (var item in contentArray)
        {
            if (item is not JsonObject contentItem)
                continue;

            // Get the content type (default to "text")
            var type = contentItem["type"]?.GetValue<string>() ?? "text";

            switch (type)
            {
                case "text":
                    if (contentItem.TryGetPropertyValue("text", out var textNode))
                    {
                        sb.AppendLine(textNode?.GetValue<string>());
                    }
                    break;

                case "image":
                    // Handle image content (just note its presence)
                    sb.AppendLine("[Image content]");
                    break;

                case "resource":
                    // Handle resource content
                    if (contentItem.TryGetPropertyValue("resource", out var resourceNode))
                    {
                        sb.AppendLine($"[Resource: {resourceNode}]");
                    }
                    break;

                default:
                    // Unknown type - serialize it
                    sb.AppendLine(contentItem.ToJsonString());
                    break;
            }
        }

        var text = sb.ToString().Trim();
        return string.IsNullOrEmpty(text) ? result.ToJsonString() : text;
    }

    private async Task<T> SendRequestAsync<T>(string method, object? parameters) where T : JsonNode
    {
        var id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pendingRequests.TryAdd(id, tcs))
        {
            throw new InvalidOperationException("Duplicate request ID");
        }

        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method
            };

            if (parameters != null)
            {
                request["params"] = JsonSerializer.SerializeToNode(parameters, JsonOptions);
            }

            var json = request.ToJsonString(JsonOptions);

            // Serialize writes to prevent interleaving
            await _writeLock.WaitAsync();
            try
            {
                await _writer.WriteLineAsync(json);
                await _writer.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            // Wait for the response (routed by the read loop)
            var result = await tcs.Task;
            return (T)result;
        }
        catch
        {
            // Clean up pending request on error
            _pendingRequests.TryRemove(id, out _);
            throw;
        }
    }

    private async Task SendNotificationAsync(string method, object? parameters)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };

        if (parameters != null)
        {
            notification["params"] = JsonSerializer.SerializeToNode(parameters, JsonOptions);
        }

        var json = notification.ToJsonString(JsonOptions);

        await _writeLock.WaitAsync();
        try
        {
            await _writer.WriteLineAsync(json);
            await _writer.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Signal background tasks to stop
        await _cts.CancelAsync();

        // Fail any pending requests
        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var tcs))
            {
                tcs.TrySetCanceled();
            }
        }

        // Wait briefly for background tasks to finish
        try
        {
            await Task.WhenAny(
                Task.WhenAll(_readLoopTask, _stderrDrainTask),
                Task.Delay(1000));
        }
        catch
        {
            // Ignore errors during cleanup
        }

        try
        {
            _process.Kill();
        }
        catch
        {
            // Ignore errors during cleanup
        }

        _process.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}

public class McpTool
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public JsonNode? InputSchema { get; set; }
}
