using System.Text.Json.Nodes;
using BinlogMcp.Client;
using BinlogMcp.Tools;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;
using Xunit;
using Xunit.Abstractions;

namespace BinlogMcp.IntegrationTests;

/// <summary>
/// Integration tests that make actual LLM API calls.
/// These tests are skipped if OPENAI_API_KEY is not set.
/// Run with: dotnet test tests/BinlogMcp.IntegrationTests
/// </summary>
public class LlmIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private readonly string? _apiKey;
    private readonly string? _binlogPath;

    public LlmIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrEmpty(_apiKey))
        {
            var config = new ConfigurationBuilder()
                .AddUserSecrets<LlmIntegrationTests>()
                .Build();
            _apiKey = config["OpenAI:ApiKey"];
        }
        _binlogPath = FindTestBinlog();
    }

    private void SkipIfNotConfigured()
    {
        Skip.If(string.IsNullOrEmpty(_apiKey), "OpenAI API key not set. Use: dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-...\" --project tests/BinlogMcp.IntegrationTests");
        Skip.If(_binlogPath == null, "test.binlog not found");
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

    private static string? FindOrchardCoreBinlog(string filename)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            var binlog = Path.Combine(dir.FullName, "test-data", "OrchardCore", filename);
            if (File.Exists(binlog))
                return binlog;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Tests that the LLM can answer a simple question about build errors.
    /// </summary>
    [SkippableFact]
    public async Task LLM_AnswersQuestionAboutBuildErrors()
    {
        SkipIfNotConfigured();

        var client = new OpenAIClient(_apiKey!);
        var chatClient = client.GetChatClient("gpt-4o-mini"); // Use cheap model for tests

        var tools = GetBinlogTools();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($"You are analyzing the build log at: {_binlogPath}"),
            new UserChatMessage("Were there any errors in this build?")
        };

        var response = await RunWithToolsAsync(chatClient, messages, tools, maxIterations: 3);

        _output.WriteLine($"Response: {response}");

        // Should have gotten a response
        Assert.False(string.IsNullOrEmpty(response));

        // Response should mention something about errors (either there are some or there aren't)
        Assert.True(
            response.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("no ", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("success", StringComparison.OrdinalIgnoreCase),
            "Response should mention errors or success");
    }

    /// <summary>
    /// Tests that the LLM can identify slow targets.
    /// </summary>
    [SkippableFact]
    public async Task LLM_IdentifiesSlowTargets()
    {
        SkipIfNotConfigured();

        var client = new OpenAIClient(_apiKey!);
        var chatClient = client.GetChatClient("gpt-4o-mini");

        var tools = GetBinlogTools();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($"You are analyzing the build log at: {_binlogPath}"),
            new UserChatMessage("What are the 3 slowest targets in this build?")
        };

        var response = await RunWithToolsAsync(chatClient, messages, tools, maxIterations: 3);

        _output.WriteLine($"Response: {response}");

        Assert.False(string.IsNullOrEmpty(response));

        // Response should mention targets or timing
        Assert.True(
            response.Contains("target", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("ms", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("second", StringComparison.OrdinalIgnoreCase),
            "Response should mention targets or timing");
    }

    /// <summary>
    /// Tests that the LLM can compare two builds and identify incremental patterns.
    /// </summary>
    [SkippableFact]
    public async Task LLM_ComparesBuildsAndIdentifiesIncrementalPattern()
    {
        SkipIfNotConfigured();

        var oc1 = FindOrchardCoreBinlog("oc-1.binlog");
        var oc2 = FindOrchardCoreBinlog("oc-2.binlog");

        Skip.If(oc1 == null || oc2 == null, "OrchardCore binlogs not found in test-data/OrchardCore/");

        var client = new OpenAIClient(_apiKey!);
        var chatClient = client.GetChatClient("gpt-4o-mini");

        var tools = GetBinlogTools();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage($"You have access to two build logs:\n- Baseline: {oc1}\n- Current: {oc2}"),
            new UserChatMessage("Compare these two builds. Is one of them an incremental build?")
        };

        var response = await RunWithToolsAsync(chatClient, messages, tools, maxIterations: 5);

        _output.WriteLine($"Response: {response}");

        Assert.False(string.IsNullOrEmpty(response));

        // Response should mention incremental or performance difference
        Assert.True(
            response.Contains("incremental", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("faster", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("cached", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("skipped", StringComparison.OrdinalIgnoreCase),
            $"Response should mention incremental build pattern. Got: {response}");
    }

    /// <summary>
    /// Helper to run chat with tool calls until complete or max iterations.
    /// </summary>
    private async Task<string> RunWithToolsAsync(
        ChatClient chatClient,
        List<ChatMessage> messages,
        List<ChatTool> tools,
        int maxIterations)
    {
        string finalResponse = "";

        for (int i = 0; i < maxIterations; i++)
        {
            var options = new ChatCompletionOptions();
            foreach (var tool in tools)
                options.Tools.Add(tool);

            var response = await chatClient.CompleteChatAsync(messages, options);
            var completion = response.Value;

            if (completion.FinishReason == ChatFinishReason.Stop)
            {
                finalResponse = completion.Content[0].Text;
                break;
            }

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                // Add assistant message with tool calls
                messages.Add(new AssistantChatMessage(completion));

                // Execute each tool call
                foreach (var toolCall in completion.ToolCalls)
                {
                    _output.WriteLine($"Tool call: {toolCall.FunctionName}({toolCall.FunctionArguments})");

                    var toolResult = ExecuteTool(toolCall.FunctionName, toolCall.FunctionArguments.ToString());
                    _output.WriteLine($"Tool result length: {toolResult.Length} chars");

                    messages.Add(new ToolChatMessage(toolCall.Id, toolResult));
                }
            }
            else
            {
                // Other finish reasons - extract any text
                if (completion.Content.Count > 0)
                    finalResponse = completion.Content[0].Text;
                break;
            }
        }

        return finalResponse;
    }

    /// <summary>
    /// Gets the list of binlog tools as ChatTools.
    /// </summary>
    private static List<ChatTool> GetBinlogTools()
    {
        return new List<ChatTool>
        {
            ChatTool.CreateFunctionTool(
                "GetBuildSummary",
                "Gets build summary: result, duration, error/warning counts",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "binlogPath": { "type": "string", "description": "Path to the binlog file" }
                        },
                        "required": ["binlogPath"]
                    }
                    """)),

            ChatTool.CreateFunctionTool(
                "GetErrors",
                "Extracts all errors with file, line, column, code, and message",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "binlogPath": { "type": "string", "description": "Path to the binlog file" }
                        },
                        "required": ["binlogPath"]
                    }
                    """)),

            ChatTool.CreateFunctionTool(
                "GetTargets",
                "Gets target execution details sorted by duration (slowest first)",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "binlogPath": { "type": "string", "description": "Path to the binlog file" },
                            "limit": { "type": "integer", "description": "Maximum targets to return", "default": 50 }
                        },
                        "required": ["binlogPath"]
                    }
                    """)),

            ChatTool.CreateFunctionTool(
                "CompareBinlogs",
                "Compares two binlogs showing timing changes, new/fixed errors, target differences, and detects incremental builds",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "baselinePath": { "type": "string", "description": "Path to the baseline (older) binlog file" },
                            "comparisonPath": { "type": "string", "description": "Path to the comparison (newer) binlog file" }
                        },
                        "required": ["baselinePath", "comparisonPath"]
                    }
                    """)),

            ChatTool.CreateFunctionTool(
                "GetIncrementalBuildAnalysis",
                "Analyzes incremental build behavior - executed vs skipped targets",
                BinaryData.FromString("""
                    {
                        "type": "object",
                        "properties": {
                            "binlogPath": { "type": "string", "description": "Path to the binlog file" }
                        },
                        "required": ["binlogPath"]
                    }
                    """))
        };
    }

    /// <summary>
    /// Executes a tool by name with the given arguments.
    /// </summary>
    private static string ExecuteTool(string toolName, string argsJson)
    {
        var args = JsonNode.Parse(argsJson)?.AsObject() ?? new JsonObject();

        return toolName switch
        {
            "GetBuildSummary" => BinlogTools.GetBuildSummary(
                args["binlogPath"]?.GetValue<string>() ?? ""),

            "GetErrors" => BinlogTools.GetErrors(
                args["binlogPath"]?.GetValue<string>() ?? ""),

            "GetTargets" => BinlogTools.GetTargets(
                args["binlogPath"]?.GetValue<string>() ?? "",
                limit: args["limit"]?.GetValue<int>() ?? 50),

            "CompareBinlogs" => BinlogTools.CompareBinlogs(
                args["baselinePath"]?.GetValue<string>() ?? "",
                args["comparisonPath"]?.GetValue<string>() ?? ""),

            "GetIncrementalBuildAnalysis" => BinlogTools.GetIncrementalBuildAnalysis(
                args["binlogPath"]?.GetValue<string>() ?? ""),

            _ => $"{{\"error\": \"Unknown tool: {toolName}\"}}"
        };
    }
}
