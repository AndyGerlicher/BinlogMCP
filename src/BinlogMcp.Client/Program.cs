using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BinlogMcp.Client;
using BinlogMcp.Visualization;
using static BinlogMcp.Client.Colors;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Responses;
using McpToolDef = BinlogMcp.Client.McpTool;

#pragma warning disable OPENAI001 // Responses API is experimental

// Client constants
const int MaxAgenticIterations = 30;
const int MaxSynthesisIterations = 10;
const int MaxOutputTokens = 16000;
const int MaxSynthesisOutputTokens = 8000;
const int MaxToolResultLength = 50000;

// Parse command line arguments
var argsList = args.ToList();
string? modeOverride = null;

// Parse --mode option
var modeIndex = argsList.FindIndex(a => a == "--mode" || a == "-m");
if (modeIndex >= 0 && modeIndex + 1 < argsList.Count)
{
    modeOverride = argsList[modeIndex + 1].ToLowerInvariant();
    argsList.RemoveAt(modeIndex + 1);
    argsList.RemoveAt(modeIndex);
}

// Parse --help
if (argsList.Any(a => a == "--help" || a == "-h" || a == "help"))
{
    PrintUsage();
    return 0;
}

// Get binlog path (optional - will prompt if not provided)
string? binlogPath = argsList.FirstOrDefault(a => !a.StartsWith("-"));

// If no binlog provided, prompt for one
if (string.IsNullOrEmpty(binlogPath))
{
    Console.Error.Write("Enter binlog path (or drag file here): ");
    binlogPath = Console.ReadLine()?.Trim().Trim('"');

    if (string.IsNullOrEmpty(binlogPath))
    {
        Console.Error.WriteLine("No binlog path provided. Exiting.");
        return 1;
    }
}

binlogPath = Path.GetFullPath(binlogPath);

if (!File.Exists(binlogPath))
{
    Console.Error.WriteLine($"Error: Binlog file not found: {binlogPath}");
    return 1;
}

// Baseline binlog for comparisons (set via "set baseline <path>")
string? baselinePath = null;

// Get API key from environment or user secrets
var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrEmpty(apiKey))
{
    var config = new ConfigurationBuilder()
        .AddUserSecrets<Program>()
        .Build();
    apiKey = config["OpenAI:ApiKey"];
}

if (string.IsNullOrEmpty(apiKey))
{
    Console.Error.WriteLine("Error: OpenAI API key not found.");
    Console.Error.WriteLine("Set OPENAI_API_KEY environment variable, or use:");
    Console.Error.WriteLine("  dotnet user-secrets set \"OpenAI:ApiKey\" \"sk-your-key\"");
    return 1;
}

// Determine execution mode
var mode = modeOverride ?? Environment.GetEnvironmentVariable("AIDAVID_MODE")?.ToLowerInvariant() ?? "fast";
if (mode != "fast" && mode != "hybrid")
{
    Console.Error.WriteLine($"Error: Invalid mode '{mode}'. Use: fast or hybrid");
    return 1;
}

var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1";
var synthesisModel = Environment.GetEnvironmentVariable("OPENAI_MODEL_SYNTHESIS") ?? "gpt-5-codex";

// Find the MCP server
var server = FindMcpServer();
if (server == null)
{
    Console.Error.WriteLine("Error: Could not find BinlogMcp server. Run 'dotnet build -c Release' first.");
    return 1;
}

// Show ASCII banner
PrintBanner();

// Initialize logging
Logger.LogSessionStart(binlogPath, mode);

Console.Error.WriteLine($"     {Colors.White}{Path.GetFileName(binlogPath)}{Colors.Reset} {Colors.Dim}loaded{Colors.Reset}");

// Start MCP client
await using var mcp = await McpClient.StartAsync(server.Value.command, server.Value.args);

// List available tools
var mcpTools = await mcp.ListToolsAsync();
Console.Error.WriteLine($"     {Colors.Dim}{mcpTools.Count} analysis tools ready{Colors.Reset}");
Console.Error.WriteLine();

Logger.LogInfo($"MCP server started with {mcpTools.Count} tools");

// Create OpenAI client
var openAiClient = new OpenAIClient(apiKey);
var statusDisplay = new StatusDisplay();

// Build the system prompt for conversation
var systemPrompt = GetInteractiveSystemPrompt(binlogPath);
var conversation = new ConversationManager(systemPrompt);

// Show welcome message
PrintWelcome();

// REPL loop
while (true)
{
    Console.Error.Write($"{Colors.BrightCyan}binlog>{Colors.Reset} ");
    var input = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(input))
        continue;

    Logger.LogUserInput(input);

    var lowerInput = input.ToLowerInvariant();

    // Handle commands
    if (lowerInput is "exit" or "quit" or "q")
    {
        Console.Error.WriteLine($"{Colors.Dim}Goodbye!{Colors.Reset}");
        Logger.LogInfo("Session ended by user");
        break;
    }

    if (lowerInput is "help" or "?" or "4")
    {
        PrintHelp();
        continue;
    }

    if (lowerInput is "clear")
    {
        conversation.Clear();
        statusDisplay.Reset();
        Console.Error.WriteLine("Conversation cleared.");
        continue;
    }

    if (lowerInput is "log")
    {
        Console.Error.WriteLine($"Log file: {Logger.GetLogFilePath()}");
        continue;
    }

    // Set baseline command
    if (lowerInput.StartsWith("set baseline ") || lowerInput.StartsWith("baseline "))
    {
        var path = input.Substring(lowerInput.StartsWith("set baseline ") ? 13 : 9).Trim().Trim('"');
        if (string.IsNullOrEmpty(path))
        {
            Console.Error.WriteLine("Usage: set baseline <path-to-binlog>");
            continue;
        }

        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Error: Baseline file not found: {path}");
            continue;
        }

        baselinePath = path;
        Console.Error.WriteLine($"{Colors.Green}Baseline set:{Colors.Reset} {Colors.White}{Path.GetFileName(baselinePath)}{Colors.Reset}");
        Console.Error.WriteLine($"{Colors.Dim}Use 'compare' or 'visualize comparison' to compare builds.{Colors.Reset}");
        continue;
    }

    if (lowerInput is "clear baseline")
    {
        baselinePath = null;
        Console.Error.WriteLine($"{Colors.Dim}Baseline cleared.{Colors.Reset}");
        continue;
    }

    // Compare command (shortcut for performance comparison)
    if (lowerInput is "compare" or "comparison")
    {
        if (baselinePath == null)
        {
            Console.Error.WriteLine("No baseline set. Use 'set baseline <path>' first.");
            continue;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"{Colors.BrightCyan}Comparing:{Colors.Reset} {Colors.Dim}{Path.GetFileName(baselinePath)} → {Path.GetFileName(binlogPath)}{Colors.Reset}");
        Console.Error.WriteLine();

        // Ask LLM to do the comparison
        var comparePrompt = $"Compare the baseline build ({baselinePath}) with the current build ({binlogPath}). " +
            "Use ComparePerformance, DiffTargetExecution, and DiffProperties to show the key differences. " +
            "Focus on timing changes, what ran differently, and any important property changes.";

        await RunConversation(
            openAiClient, model, comparePrompt,
            mcpTools, mcp, conversation, statusDisplay);

        Console.Error.WriteLine();
        continue;
    }

    // Visualization commands
    if (lowerInput.StartsWith("visualize ") || lowerInput.StartsWith("viz ") || lowerInput is "2" or "3")
    {
        var vizType = lowerInput switch
        {
            "2" => "timeline",
            "3" => "slowest",
            _ => lowerInput.Substring(lowerInput.StartsWith("visualize ") ? 10 : 4).Trim()
        };

        await HandleVisualize(vizType, binlogPath, baselinePath, mcp, statusDisplay);
        continue;
    }

    if (lowerInput is "diagnose" or "yes" or "y" or "1")
    {
        // Run AIDavid diagnosis
        Console.Error.WriteLine();
        Console.Error.WriteLine($"{Colors.BrightCyan}Running AIDavid diagnosis...{Colors.Reset}");
        Console.Error.WriteLine();

        await RunDiagnosis(
            openAiClient, model, synthesisModel, mode, binlogPath,
            mcpTools, mcp, statusDisplay);

        Console.Error.WriteLine();
        continue;
    }

    // Regular conversation - ask a question
    Console.Error.WriteLine();

    await RunConversation(
        openAiClient, model, input,
        mcpTools, mcp, conversation, statusDisplay);

    Console.Error.WriteLine();
}

Logger.Flush();
return 0;

// ============================================================================
// Interactive conversation with history (using Responses API)
// ============================================================================

static async Task RunConversation(
    OpenAIClient openAiClient,
    string model,
    string userInput,
    List<McpToolDef> mcpTools,
    McpClient mcp,
    ConversationManager conversation,
    StatusDisplay statusDisplay)
{
    conversation.AddUserMessage(userInput);

    var responsesClient = openAiClient.GetResponsesClient(model);

    // Convert MCP tools to Responses API function tools
    var tools = mcpTools.Select(t =>
    {
        var schema = t.InputSchema?.AsObject() ?? new JsonObject();
        if (!schema.ContainsKey("type"))
            schema["type"] = "object";

        return ResponseTool.CreateFunctionTool(
            t.Name,
            BinaryData.FromString(schema.ToJsonString()),
            strictModeEnabled: false,
            functionDescription: t.Description);
    }).ToList();

    var options = new CreateResponseOptions
    {
        Instructions = conversation.SystemPrompt,
        MaxOutputTokenCount = MaxOutputTokens
    };
    foreach (var tool in tools)
        options.Tools.Add(tool);

    // Add conversation history as input items
    foreach (var item in conversation.GetResponseItems())
        options.InputItems.Add(item);

    string? previousResponseId = null;
    int totalToolCalls = 0;

    for (int i = 0; i < MaxAgenticIterations; i++)
    {
        Logger.LogIteration(i + 1, MaxAgenticIterations, totalToolCalls);
        statusDisplay.ShowStatus();

        if (previousResponseId != null)
        {
            options.PreviousResponseId = previousResponseId;
        }

        var response = await responsesClient.CreateResponseAsync(options);
        var result = response.Value;

        Logger.LogLlmResponse(model, result.Status?.ToString() ?? "Unknown");

        if (result.Status == ResponseStatus.Failed)
        {
            statusDisplay.ClearStatus();
            Logger.LogError($"Response failed: {result.Error?.Message}");
            Console.Error.WriteLine($"Response failed: {result.Error?.Message ?? "Unknown error"}");
            break;
        }

        var functionCalls = new List<FunctionCallResponseItem>();
        var textOutputBuilder = new StringBuilder();

        foreach (var item in result.OutputItems)
        {
            switch (item)
            {
                case FunctionCallResponseItem funcCall:
                    functionCalls.Add(funcCall);
                    break;

                case MessageResponseItem message:
                    foreach (var content in message.Content)
                    {
                        if (content.Kind == ResponseContentPartKind.OutputText && !string.IsNullOrEmpty(content.Text))
                        {
                            textOutputBuilder.Append(content.Text);
                        }
                    }
                    break;
            }
        }

        var textOutput = textOutputBuilder.ToString();

        if (functionCalls.Count > 0)
        {
            previousResponseId = result.Id;
            options.InputItems.Clear();

            foreach (var funcCall in functionCalls)
            {
                totalToolCalls++;
                statusDisplay.ShowStatus();

                var argsJson = JsonNode.Parse(funcCall.FunctionArguments.ToString()) as JsonObject
                    ?? new JsonObject();

                Logger.LogToolCall(funcCall.FunctionName, argsJson.ToJsonString());

                try
                {
                    var toolResult = await mcp.CallToolAsync(funcCall.FunctionName, argsJson);

                    if (toolResult.Length > MaxToolResultLength)
                    {
                        toolResult = toolResult[..MaxToolResultLength] + "\n\n[Output truncated due to length]";
                    }

                    Logger.LogToolResult(funcCall.FunctionName, toolResult);
                    options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(funcCall.CallId, toolResult));
                }
                catch (Exception ex)
                {
                    Logger.LogToolResult(funcCall.FunctionName, ex.Message, isError: true);
                    options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                        funcCall.CallId,
                        $"Error calling tool: {ex.Message}"));
                }
            }
        }
        else if (!string.IsNullOrEmpty(textOutput))
        {
            statusDisplay.ClearStatus();
            conversation.AddAssistantMessage(textOutput);
            Logger.LogCompletion(totalToolCalls, textOutput);
            MarkdownRenderer.Render(textOutput);
            return;
        }
        else if (result.Status == ResponseStatus.Completed)
        {
            statusDisplay.ClearStatus();
            Logger.LogWarning("Response completed with no recognized output");
            Console.Error.WriteLine("Warning: Response completed with no recognized output.");
            return;
        }
    }

    statusDisplay.ClearStatus();
    Logger.LogWarning($"Reached maximum iterations ({MaxAgenticIterations})");
    Console.Error.WriteLine($"Warning: Reached maximum iterations ({MaxAgenticIterations}) with {totalToolCalls} tool calls.");
}

// ============================================================================
// AIDavid diagnosis mode
// ============================================================================

static async Task RunDiagnosis(
    OpenAIClient openAiClient,
    string model,
    string synthesisModel,
    string mode,
    string binlogPath,
    List<McpToolDef> mcpTools,
    McpClient mcp,
    StatusDisplay statusDisplay)
{
    var systemPrompt = GetAiDavidSystemPrompt(binlogPath);
    var userPrompt = "Analyze this build and provide a comprehensive diagnosis.";

    Logger.LogInfo($"Starting AIDavid diagnosis in {mode} mode");

    switch (mode)
    {
        case "hybrid":
            await RunHybridLoop(openAiClient, model, synthesisModel, systemPrompt, userPrompt, mcpTools, mcp, statusDisplay);
            break;

        case "fast":
        default:
            await RunResponsesApiLoop(openAiClient, model, systemPrompt, userPrompt, mcpTools, mcp, statusDisplay);
            break;
    }
}

// ============================================================================
// Responses API agentic loop
// ============================================================================

static async Task RunResponsesApiLoop(
    OpenAIClient openAiClient,
    string model,
    string systemPrompt,
    string userPrompt,
    List<McpToolDef> mcpTools,
    McpClient mcp,
    StatusDisplay statusDisplay)
{
    var responsesClient = openAiClient.GetResponsesClient(model);

    var tools = mcpTools.Select(t =>
    {
        var schema = t.InputSchema?.AsObject() ?? new JsonObject();
        if (!schema.ContainsKey("type"))
            schema["type"] = "object";

        return ResponseTool.CreateFunctionTool(
            t.Name,
            BinaryData.FromString(schema.ToJsonString()),
            strictModeEnabled: false,
            functionDescription: t.Description);
    }).ToList();

    var options = new CreateResponseOptions
    {
        Instructions = systemPrompt,
        MaxOutputTokenCount = MaxOutputTokens
    };
    foreach (var tool in tools)
        options.Tools.Add(tool);

    options.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));

    string? previousResponseId = null;
    int totalToolCalls = 0;

    for (int i = 0; i < MaxAgenticIterations; i++)
    {
        Logger.LogIteration(i + 1, MaxAgenticIterations, totalToolCalls);
        statusDisplay.ShowStatus();

        if (previousResponseId != null)
        {
            options.PreviousResponseId = previousResponseId;
        }

        var response = await responsesClient.CreateResponseAsync(options);
        var result = response.Value;

        Logger.LogLlmResponse(model, result.Status?.ToString() ?? "Unknown");

        if (result.Status == ResponseStatus.Failed)
        {
            statusDisplay.ClearStatus();
            Logger.LogError($"Response failed: {result.Error?.Message}");
            Console.Error.WriteLine($"Response failed: {result.Error?.Message ?? "Unknown error"}");
            break;
        }

        var functionCalls = new List<FunctionCallResponseItem>();
        var textOutputBuilder = new StringBuilder();

        foreach (var item in result.OutputItems)
        {
            switch (item)
            {
                case FunctionCallResponseItem funcCall:
                    functionCalls.Add(funcCall);
                    break;

                case MessageResponseItem message:
                    foreach (var content in message.Content)
                    {
                        if (content.Kind == ResponseContentPartKind.OutputText && !string.IsNullOrEmpty(content.Text))
                        {
                            textOutputBuilder.Append(content.Text);
                        }
                    }
                    break;
            }
        }

        var textOutput = textOutputBuilder.ToString();

        if (functionCalls.Count > 0)
        {
            previousResponseId = result.Id;
            options.InputItems.Clear();

            foreach (var funcCall in functionCalls)
            {
                totalToolCalls++;
                statusDisplay.ShowStatus();

                var argsJson = JsonNode.Parse(funcCall.FunctionArguments.ToString()) as JsonObject
                    ?? new JsonObject();

                Logger.LogToolCall(funcCall.FunctionName, argsJson.ToJsonString());

                try
                {
                    var toolResult = await mcp.CallToolAsync(funcCall.FunctionName, argsJson);

                    if (toolResult.Length > MaxToolResultLength)
                    {
                        toolResult = toolResult[..MaxToolResultLength] + "\n\n[Output truncated due to length]";
                    }

                    Logger.LogToolResult(funcCall.FunctionName, toolResult);
                    options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(funcCall.CallId, toolResult));
                }
                catch (Exception ex)
                {
                    Logger.LogToolResult(funcCall.FunctionName, ex.Message, isError: true);
                    options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                        funcCall.CallId,
                        $"Error calling tool: {ex.Message}"));
                }
            }
        }
        else if (!string.IsNullOrEmpty(textOutput))
        {
            statusDisplay.ClearStatus();
            Logger.LogCompletion(totalToolCalls, textOutput);
            MarkdownRenderer.Render(textOutput);
            return;
        }
        else if (result.Status == ResponseStatus.Completed)
        {
            statusDisplay.ClearStatus();
            Logger.LogWarning("Response completed with no recognized output");
            Console.Error.WriteLine("Warning: Response completed with no recognized output.");
            return;
        }
    }

    statusDisplay.ClearStatus();
    Logger.LogWarning($"Reached maximum iterations ({MaxAgenticIterations})");
    Console.Error.WriteLine($"Warning: Reached maximum iterations ({MaxAgenticIterations}) with {totalToolCalls} tool calls.");
}

// ============================================================================
// Responses API internal loop (returns tool results for hybrid mode)
// ============================================================================

static async Task<(string? finalResponse, List<(string tool, string args, string result)> toolResults)> RunResponsesApiLoopInternal(
    OpenAIClient openAiClient,
    string model,
    string systemPrompt,
    string userPrompt,
    List<McpToolDef> mcpTools,
    McpClient mcp,
    StatusDisplay statusDisplay)
{
    var responsesClient = openAiClient.GetResponsesClient(model);
    var collectedToolResults = new List<(string tool, string args, string result)>();

    var tools = mcpTools.Select(t =>
    {
        var schema = t.InputSchema?.AsObject() ?? new JsonObject();
        if (!schema.ContainsKey("type"))
            schema["type"] = "object";

        return ResponseTool.CreateFunctionTool(
            t.Name,
            BinaryData.FromString(schema.ToJsonString()),
            strictModeEnabled: false,
            functionDescription: t.Description);
    }).ToList();

    var options = new CreateResponseOptions
    {
        Instructions = systemPrompt,
        MaxOutputTokenCount = MaxOutputTokens
    };
    foreach (var tool in tools)
        options.Tools.Add(tool);

    options.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));

    string? previousResponseId = null;
    int totalToolCalls = 0;

    for (int i = 0; i < MaxAgenticIterations; i++)
    {
        Logger.LogIteration(i + 1, MaxAgenticIterations, totalToolCalls);
        statusDisplay.ShowStatus();

        if (previousResponseId != null)
        {
            options.PreviousResponseId = previousResponseId;
        }

        var response = await responsesClient.CreateResponseAsync(options);
        var result = response.Value;

        Logger.LogLlmResponse(model, result.Status?.ToString() ?? "Unknown");

        if (result.Status == ResponseStatus.Failed)
        {
            statusDisplay.ClearStatus();
            Logger.LogError($"Response failed: {result.Error?.Message}");
            return (null, collectedToolResults);
        }

        var functionCalls = new List<FunctionCallResponseItem>();
        var textOutputBuilder = new StringBuilder();

        foreach (var item in result.OutputItems)
        {
            switch (item)
            {
                case FunctionCallResponseItem funcCall:
                    functionCalls.Add(funcCall);
                    break;

                case MessageResponseItem message:
                    foreach (var content in message.Content)
                    {
                        if (content.Kind == ResponseContentPartKind.OutputText && !string.IsNullOrEmpty(content.Text))
                        {
                            textOutputBuilder.Append(content.Text);
                        }
                    }
                    break;
            }
        }

        var textOutput = textOutputBuilder.ToString();

        if (functionCalls.Count > 0)
        {
            previousResponseId = result.Id;
            options.InputItems.Clear();

            foreach (var funcCall in functionCalls)
            {
                totalToolCalls++;
                statusDisplay.ShowStatus();

                var argsJson = JsonNode.Parse(funcCall.FunctionArguments.ToString()) as JsonObject
                    ?? new JsonObject();
                var argsString = argsJson.ToJsonString();

                Logger.LogToolCall(funcCall.FunctionName, argsString);

                try
                {
                    var toolResult = await mcp.CallToolAsync(funcCall.FunctionName, argsJson);

                    collectedToolResults.Add((funcCall.FunctionName, argsString, toolResult));

                    if (toolResult.Length > MaxToolResultLength)
                    {
                        toolResult = toolResult[..MaxToolResultLength] + "\n\n[Output truncated due to length]";
                    }

                    Logger.LogToolResult(funcCall.FunctionName, toolResult);
                    options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(funcCall.CallId, toolResult));
                }
                catch (Exception ex)
                {
                    Logger.LogToolResult(funcCall.FunctionName, ex.Message, isError: true);
                    options.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(
                        funcCall.CallId,
                        $"Error calling tool: {ex.Message}"));
                    collectedToolResults.Add((funcCall.FunctionName, argsString, $"Error: {ex.Message}"));
                }
            }
        }
        else if (!string.IsNullOrEmpty(textOutput))
        {
            statusDisplay.ClearStatus();
            Logger.LogCompletion(totalToolCalls, textOutput);
            return (textOutput, collectedToolResults);
        }
        else if (result.Status == ResponseStatus.Completed)
        {
            statusDisplay.ClearStatus();
            Logger.LogWarning("Response completed with no recognized output");
            return (null, collectedToolResults);
        }
    }

    statusDisplay.ClearStatus();
    Logger.LogWarning($"Reached maximum iterations ({MaxAgenticIterations})");
    return (null, collectedToolResults);
}

// ============================================================================
// Hybrid mode: default model for tool calls, synthesis model for final analysis
// ============================================================================

static async Task RunHybridLoop(
    OpenAIClient openAiClient,
    string model,
    string synthesisModel,
    string systemPrompt,
    string userPrompt,
    List<McpToolDef> mcpTools,
    McpClient mcp,
    StatusDisplay statusDisplay)
{
    Console.Error.WriteLine($"{Colors.Dim}Phase 1: Gathering information with {model}...{Colors.Reset}");
    Console.Error.WriteLine();

    var gatheringPrompt = GetAiDavidGatheringPrompt(userPrompt);

    var (initialAnalysis, toolResults) = await RunResponsesApiLoopInternal(
        openAiClient, model, systemPrompt + "\n\n" + gatheringPrompt, userPrompt, mcpTools, mcp, statusDisplay);

    if (toolResults.Count == 0)
    {
        Console.Error.WriteLine("No tools were called. Using model output directly.");
        if (!string.IsNullOrEmpty(initialAnalysis))
        {
            MarkdownRenderer.Render(initialAnalysis);
        }
        return;
    }

    Console.Error.WriteLine();
    Console.Error.WriteLine($"{Colors.Dim}Phase 2: Synthesizing with {synthesisModel}...{Colors.Reset}");
    Console.Error.WriteLine();

    var contextBuilder = new StringBuilder();
    contextBuilder.AppendLine("# Build Analysis Context");
    contextBuilder.AppendLine();
    contextBuilder.AppendLine("The following information was gathered from analyzing the binlog:");
    contextBuilder.AppendLine();

    foreach (var (tool, args, result) in toolResults)
    {
        contextBuilder.AppendLine($"## {tool}");
        if (!string.IsNullOrEmpty(args) && args != "{}")
        {
            contextBuilder.AppendLine($"Arguments: {args}");
        }
        contextBuilder.AppendLine();
        contextBuilder.AppendLine("```json");
        var truncatedResult = result.Length > 15000
            ? result[..15000] + "\n... [truncated]"
            : result;
        contextBuilder.AppendLine(truncatedResult);
        contextBuilder.AppendLine("```");
        contextBuilder.AppendLine();
    }

    if (!string.IsNullOrEmpty(initialAnalysis))
    {
        contextBuilder.AppendLine("## Initial Analysis");
        contextBuilder.AppendLine(initialAnalysis);
        contextBuilder.AppendLine();
    }

    var synthesisPrompt = GetAiDavidSynthesisPrompt();

    var responsesClient = openAiClient.GetResponsesClient(synthesisModel);

    var options = new CreateResponseOptions
    {
        Instructions = synthesisPrompt,
        MaxOutputTokenCount = MaxSynthesisOutputTokens
    };

    options.InputItems.Add(ResponseItem.CreateUserMessageItem(contextBuilder.ToString()));

    try
    {
        string? previousResponseId = null;
        var synthesisOutput = new StringBuilder();

        for (int i = 0; i < MaxSynthesisIterations; i++)
        {
            statusDisplay.ShowStatus("Synthesizing...");

            if (previousResponseId != null)
            {
                options.PreviousResponseId = previousResponseId;
                options.InputItems.Clear();
            }

            var response = await responsesClient.CreateResponseAsync(options);
            var result = response.Value;

            Logger.LogLlmResponse(synthesisModel, result.Status?.ToString() ?? "Unknown");

            if (result.Status == ResponseStatus.Failed)
            {
                statusDisplay.ClearStatus();
                Logger.LogError($"Synthesis failed: {result.Error?.Message}");
                break;
            }

            foreach (var item in result.OutputItems)
            {
                if (item is MessageResponseItem message)
                {
                    foreach (var content in message.Content)
                    {
                        if (content.Kind == ResponseContentPartKind.OutputText && !string.IsNullOrEmpty(content.Text))
                        {
                            synthesisOutput.Append(content.Text);
                        }
                    }
                }
            }

            if (result.Status == ResponseStatus.Completed)
            {
                Logger.LogDebug($"Synthesis completed after {i + 1} iteration(s)");
                break;
            }

            if (result.Status == ResponseStatus.Incomplete)
            {
                previousResponseId = result.Id;
                continue;
            }

            Logger.LogWarning($"Unexpected synthesis status: {result.Status}");
            break;
        }

        statusDisplay.ClearStatus();

        if (synthesisOutput.Length > 0)
        {
            Logger.LogCompletion(toolResults.Count, synthesisOutput.ToString());
            MarkdownRenderer.Render(synthesisOutput.ToString());
        }
        else
        {
            Console.Error.WriteLine("Warning: Synthesis returned no text output.");
            if (!string.IsNullOrEmpty(initialAnalysis))
            {
                Console.Error.WriteLine("Falling back to initial model analysis:");
                MarkdownRenderer.Render(initialAnalysis);
            }
        }
    }
    catch (Exception ex)
    {
        statusDisplay.ClearStatus();
        Logger.LogError($"Error during synthesis: {ex.Message}", ex);

        if (!string.IsNullOrEmpty(initialAnalysis))
        {
            Console.Error.WriteLine("Falling back to initial model analysis:");
            MarkdownRenderer.Render(initialAnalysis);
        }
    }
}

// ============================================================================
// System prompts
// ============================================================================

static string GetInteractiveSystemPrompt(string binlogPath) => $"""
    You are an MSBuild binary log analyzer with access to powerful analysis tools.

    The user is analyzing this binlog file: {binlogPath}

    When the user asks questions, use the available tools to gather information and provide helpful answers.
    When calling tools that require a binlog path, always use: {binlogPath}

    ## Understanding Build Structure

    When analyzing performance or timing:
    - **dirs.proj, *.sln, *.slnx files** are aggregation/traversal projects that orchestrate building many projects.
      It's expected and correct that they span most or all of the build time - they're not the bottleneck themselves.
    - When these appear as "slow", dig into the actual projects they contain to find the real bottlenecks.
    - Use GetProjectDependencies or GetProjectPerformance to see the individual projects and their timings.
    - The interesting analysis is which actual projects (*.csproj, *.vbproj, etc.) are slow, not the aggregators.

    Be concise but thorough. If you find errors or issues, explain what they mean and suggest fixes.
    Remember the context from previous questions in this conversation.
    """;

static string GetAiDavidGatheringPrompt(string userPrompt) => """
    IMPORTANT: Your role in this phase is to GATHER INFORMATION using the available tools.
    Do NOT provide a final diagnosis yet. Just call the relevant tools to collect data.

    After gathering information, provide a brief summary of what you found, but note that
    a more thorough analysis will follow.

    Start by calling GetBuildSummary, then based on what you find, call additional tools
    to investigate errors, performance, or other issues.
    """;

static string GetAiDavidSynthesisPrompt() => """
    You are AIDavid, a virtual MSBuild debugging expert inspired by David Federman's methodology.

    You have been provided with detailed analysis data gathered from an MSBuild binary log.
    Your job is to synthesize this information into a clear, actionable diagnosis.

    ## Understanding Build Structure

    When discussing performance:
    - **dirs.proj, *.sln, *.slnx files** are aggregation projects that orchestrate building many projects.
      They're expected to span the entire build time - this is normal, not a problem.
    - Focus on actual projects (*.csproj, *.vbproj, etc.) when identifying bottlenecks.
    - Don't suggest "optimizing" dirs.proj or solution files - they're just orchestrators.

    ## Your Output Format

    Structure your diagnosis as:

    ## Build Overview
    [Quick summary of what this build was trying to do and the outcome]

    ## What Happened
    [Factual description of the build execution, errors, key events]

    ## Why It Happened
    [Root cause analysis - trace the issue back to its source]

    ## Recommendations
    [Specific, actionable steps to fix the issue, ordered by priority]

    ## Additional Observations (if relevant)
    [Performance issues, warnings worth addressing, other improvements]

    ---

    Be direct, specific, and actionable. Developers should be able to take your recommendations
    and immediately start fixing issues. Explain MSBuild concepts when they're not obvious.
    """;

static string GetAiDavidSystemPrompt(string binlogPath) => $"""
    You are AIDavid, a virtual MSBuild debugging expert inspired by David Federman's methodology.

    Your debugging philosophy follows David's approach from his influential guide on MSBuild debugging:

    ## The Two Questions

    Every MSBuild investigation answers two fundamental questions:
    1. **"What happened?"** - Examine target execution, property values, items, and actual build results
    2. **"Why did it happen?"** - Trace the logic: imports, conditions, property origins, target dependencies

    ## Understanding Build Structure

    When analyzing performance or timing:
    - **dirs.proj, *.sln, *.slnx files** are aggregation/traversal projects that orchestrate building many projects.
      It's expected and correct that they span most or all of the build time - they're not bottlenecks themselves.
    - Don't report these as "slow" - they're supposed to encompass the entire build.
    - Instead, use GetProjectPerformance or GetProjectDependencies to find the actual slow projects underneath.
    - Focus your performance analysis on real projects (*.csproj, *.vbproj, *.fsproj) not aggregators.

    ## Your Investigation Process

    For this binlog: {binlogPath}

    ### Phase 1: Understand the Build Outcome
    - Start with GetBuildSummary to understand success/failure, duration, scope
    - If failed, use GetErrors to see exact error messages and locations
    - Use GetFailureDiagnosis for categorized root cause analysis

    ### Phase 2: Investigate "What Happened"
    - Use GetTargets/GetTasks to see what executed and timing
    - Use GetProperties and GetItems to examine key values
    - Use GetTargetExecutionReasons to understand target flow
    - Use GetSkippedTargets to see what DIDN'T run and why

    ### Phase 3: Investigate "Why It Happened"
    - Use GetPropertyOrigin to trace important properties to their source files
    - Use GetImportChain to understand the project structure and imports
    - Use SearchBinlog to find specific values or patterns
    - For incremental build issues, use GetIncrementalBuildAnalysis

    ### Phase 4: Synthesize and Recommend
    - Connect the dots between symptoms and root causes
    - Provide specific, actionable fix recommendations
    - Explain the "why" behind each recommendation

    ## Your Personality

    You are knowledgeable, methodical, and genuinely helpful. You explain MSBuild concepts clearly
    because you know most developers don't work with it daily. You don't just say what's wrong -
    you explain WHY and HOW to fix it.

    ## Output Format

    Structure your diagnosis as:

    ## Build Overview
    [Quick summary of what this build was trying to do and the outcome]

    ## What Happened
    [Factual description of the build execution, errors, key events]

    ## Why It Happened
    [Root cause analysis - trace the issue back to its source]

    ## Recommendations
    [Specific, actionable steps to fix the issue]

    ## Additional Observations (if relevant)
    [Performance issues, warnings worth addressing, other improvements]

    ---

    Now analyze the build thoroughly. Be the debugging expert developers wish they had access to.
    """;

// ============================================================================
// Helper functions
// ============================================================================

static void PrintBanner()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {Colors.BrightCyan}{Colors.Bold}▐█▌ BinlogMCP{Colors.Reset} {Colors.Dim}───────────────────────────────────{Colors.Reset}");
    Console.Error.WriteLine($"  {Colors.Yellow}🔨{Colors.Reset} {Colors.White}Build Log Investigator{Colors.Reset}");
    Console.Error.WriteLine($"     {Colors.Dim}inspired by dfederm.com/debugging-msbuild{Colors.Reset}");
    Console.Error.WriteLine();
}

static void PrintWelcome()
{
    Console.Error.WriteLine($"  {Colors.Dim}───────────────────────────────────────────────────{Colors.Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {Colors.White}Quick start:{Colors.Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"    {Colors.BrightGreen}[1]{Colors.Reset} {Colors.Green}diagnose{Colors.Reset}            Automated build analysis");
    Console.Error.WriteLine($"    {Colors.BrightGreen}[2]{Colors.Reset} {Colors.Green}visualize timeline{Colors.Reset}  Gantt chart of execution");
    Console.Error.WriteLine($"    {Colors.BrightGreen}[3]{Colors.Reset} {Colors.Green}visualize slowest{Colors.Reset}   Slowest targets chart");
    Console.Error.WriteLine($"    {Colors.BrightGreen}[4]{Colors.Reset} {Colors.Green}help{Colors.Reset}                All commands");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {Colors.Dim}Or ask: \"Why did this build fail?\"{Colors.Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {Colors.Dim}───────────────────────────────────────────────────{Colors.Reset}");
    Console.Error.WriteLine();
}

static void PrintHelp()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{Colors.White}{Colors.Bold}Commands{Colors.Reset}");
    Console.Error.WriteLine($"  {Colors.Green}diagnose{Colors.Reset}              Run automated AIDavid diagnosis");
    Console.Error.WriteLine($"  {Colors.Green}clear{Colors.Reset}                 Clear conversation history");
    Console.Error.WriteLine($"  {Colors.Green}log{Colors.Reset}                   Show log file path");
    Console.Error.WriteLine($"  {Colors.Green}help{Colors.Reset}                  Show this help");
    Console.Error.WriteLine($"  {Colors.Green}exit{Colors.Reset}                  Exit the program");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{Colors.White}{Colors.Bold}Visualization{Colors.Reset}");
    Console.Error.WriteLine($"  {Colors.Cyan}visualize timeline{Colors.Reset}    Gantt chart of build execution");
    Console.Error.WriteLine($"  {Colors.Cyan}visualize slowest{Colors.Reset}     Bar chart of slowest targets");
    Console.Error.WriteLine($"  {Colors.Cyan}visualize comparison{Colors.Reset}  Compare with baseline {Colors.Dim}(requires baseline){Colors.Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{Colors.White}{Colors.Bold}Comparison{Colors.Reset}");
    Console.Error.WriteLine($"  {Colors.Yellow}set baseline <path>{Colors.Reset}   Set a baseline binlog for comparison");
    Console.Error.WriteLine($"  {Colors.Yellow}clear baseline{Colors.Reset}        Remove the baseline");
    Console.Error.WriteLine($"  {Colors.Yellow}compare{Colors.Reset}               Compare current build with baseline");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{Colors.Dim}Or just ask a question about your build:{Colors.Reset}");
    Console.Error.WriteLine($"{Colors.Dim}  \"Why did this build fail?\"");
    Console.Error.WriteLine($"  \"What targets took the longest?\"");
    Console.Error.WriteLine($"  \"Show me the errors\"{Colors.Reset}");
    Console.Error.WriteLine();
}

static void PrintUsage()
{
    Console.Error.WriteLine("BinlogMCP - MSBuild Binary Log Analyzer");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  BinlogMcp.Client [binlog-path] [options]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("If no binlog path is provided, you will be prompted to enter one.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --mode, -m <mode>    Execution mode (default: fast)");
    Console.Error.WriteLine("                       fast   - Use default model for everything");
    Console.Error.WriteLine("                       hybrid - Use default model for tools, synthesis model for final analysis");
    Console.Error.WriteLine("  --help, -h           Show this help");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Examples:");
    Console.Error.WriteLine("  BinlogMcp.Client ./build.binlog");
    Console.Error.WriteLine("  BinlogMcp.Client ./build.binlog --mode hybrid");
    Console.Error.WriteLine("  BinlogMcp.Client");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Environment variables:");
    Console.Error.WriteLine("  OPENAI_API_KEY          - Your OpenAI API key (required)");
    Console.Error.WriteLine("  AIDAVID_MODE            - Default mode: fast or hybrid");
    Console.Error.WriteLine("  OPENAI_MODEL            - Default model (default: gpt-4.1)");
    Console.Error.WriteLine("  OPENAI_MODEL_SYNTHESIS  - Model for hybrid synthesis step (default: gpt-5-codex)");
}

// ============================================================================
// Visualization commands
// ============================================================================

static async Task HandleVisualize(string vizType, string binlogPath, string? baselinePath, McpClient mcp, StatusDisplay statusDisplay)
{
    try
    {
        switch (vizType)
        {
            case "timeline":
                await VisualizeTimeline(binlogPath, mcp, statusDisplay);
                break;

            case "slowest":
            case "performance":
            case "perf":
                await VisualizeSlowest(binlogPath, mcp, statusDisplay);
                break;

            case "comparison":
            case "compare":
            case "diff":
                if (baselinePath == null)
                {
                    Console.Error.WriteLine("No baseline set. Use 'set baseline <path>' first.");
                    return;
                }
                await VisualizeComparison(binlogPath, baselinePath, mcp, statusDisplay);
                break;

            default:
                Console.Error.WriteLine($"Unknown visualization type: {vizType}");
                Console.Error.WriteLine("Available: timeline, slowest, comparison");
                break;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Visualization error: {ex.Message}");
        Logger.LogError($"Visualization error: {ex.Message}", ex);
    }
}

static async Task VisualizeTimeline(string binlogPath, McpClient mcp, StatusDisplay statusDisplay)
{
    statusDisplay.ShowStatus("Generating timeline...");

    // Get timeline data from MCP
    var args = new JsonObject { ["binlogPath"] = binlogPath, ["level"] = "targets" };
    var json = await mcp.CallToolAsync("GetTimeline", args);

    statusDisplay.ClearStatus();

    // Parse and render
    var data = ChartRenderer.ParseTimelineJson(json, $"Build Timeline - {Path.GetFileName(binlogPath)}");
    var result = ChartRenderer.RenderAndOpenTimeline(data);

    Console.Error.WriteLine($"{Colors.Green}Timeline saved:{Colors.Reset} {Colors.Dim}{result.FilePath}{Colors.Reset}");
    Console.Error.WriteLine($"{Colors.Dim}Opening in browser...{Colors.Reset}");
}

static async Task VisualizeSlowest(string binlogPath, McpClient mcp, StatusDisplay statusDisplay)
{
    statusDisplay.ShowStatus("Analyzing performance...");

    // Get target performance data
    var args = new JsonObject { ["binlogPath"] = binlogPath, ["top"] = 30 };
    var json = await mcp.CallToolAsync("GetTargets", args);

    statusDisplay.ClearStatus();

    // Parse and render
    var data = ChartRenderer.ParsePerformanceJson(json, $"Slowest Targets - {Path.GetFileName(binlogPath)}", "targets");
    var result = ChartRenderer.RenderAndOpenBarChart(data);

    Console.Error.WriteLine($"{Colors.Green}Chart saved:{Colors.Reset} {Colors.Dim}{result.FilePath}{Colors.Reset}");
    Console.Error.WriteLine($"{Colors.Dim}Opening in browser...{Colors.Reset}");
}

static async Task VisualizeComparison(string currentPath, string baselinePath, McpClient mcp, StatusDisplay statusDisplay)
{
    statusDisplay.ShowStatus("Comparing builds...");

    // Get target performance for both builds
    var baselineArgs = new JsonObject { ["binlogPath"] = baselinePath, ["top"] = 30 };
    var currentArgs = new JsonObject { ["binlogPath"] = currentPath, ["top"] = 30 };

    var baselineJson = await mcp.CallToolAsync("GetTargets", baselineArgs);
    var currentJson = await mcp.CallToolAsync("GetTargets", currentArgs);

    statusDisplay.ClearStatus();

    // Parse and render comparison
    var data = ChartRenderer.ParseComparisonJson(
        baselineJson,
        currentJson,
        Path.GetFileName(baselinePath),
        Path.GetFileName(currentPath),
        "Build Performance Comparison",
        "targets");

    var result = ChartRenderer.RenderAndOpenBarChart(data);

    Console.Error.WriteLine($"{Colors.Green}Comparison saved:{Colors.Reset} {Colors.Dim}{result.FilePath}{Colors.Reset}");
    Console.Error.WriteLine($"{Colors.Dim}Opening in browser...{Colors.Reset}");
}

static (string command, string[] args)? FindMcpServer()
{
    var searchDirs = new[]
    {
        AppContext.BaseDirectory,
        Directory.GetCurrentDirectory(),
        Path.GetDirectoryName(Environment.ProcessPath)
    };

    foreach (var startDir in searchDirs.Where(d => !string.IsNullOrEmpty(d)))
    {
        var dir = new DirectoryInfo(startDir!);
        while (dir != null)
        {
            // First try to find a built executable (faster, no dotnet output noise)
            var exePath = Path.Combine(dir.FullName, "src", "BinlogMcp", "bin", "Release", "net10.0", "BinlogMcp.exe");
            if (File.Exists(exePath))
            {
                return (exePath, []);
            }

            // Fall back to Debug build
            exePath = Path.Combine(dir.FullName, "src", "BinlogMcp", "bin", "Debug", "net10.0", "BinlogMcp.exe");
            if (File.Exists(exePath))
            {
                return (exePath, []);
            }

            // Fall back to dotnet run (slower, may have output noise)
            var projectPath = Path.Combine(dir.FullName, "src", "BinlogMcp", "BinlogMcp.csproj");
            if (File.Exists(projectPath))
            {
                return ("dotnet", ["run", "--project", projectPath, "--no-build", "-c", "Release"]);
            }

            dir = dir.Parent;
        }
    }

    return null;
}
