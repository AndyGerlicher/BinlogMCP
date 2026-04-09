using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using BinlogMcp.Client;
using BinlogMcp.Visualization;
using static BinlogMcp.Client.Colors;
using GitHub.Copilot.SDK;

// Parse command line arguments
var argsList = args.ToList();

// Parse --help
if (argsList.Any(a => a is "--help" or "-h" or "help"))
{
    PrintUsage();
    return 0;
}

// Get binlog path (optional - will prompt if not provided)
string? binlogPath = argsList.FirstOrDefault(a => !a.StartsWith('-'));

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

// Find the MCP server executable
var server = FindMcpServer();
if (server == null)
{
    Console.Error.WriteLine("Error: Could not find BinlogMcp server. Run 'dotnet build' first.");
    return 1;
}

// Show ASCII banner
PrintBanner();

// Initialize logging
Logger.LogSessionStart(binlogPath, "copilot-sdk");

Console.Error.WriteLine($"     {White}{Path.GetFileName(binlogPath)}{Reset} {Dim}loaded{Reset}");

// Start direct MCP client for visualization commands
await using var directMcp = await McpClient.StartAsync(server.Value.command, server.Value.args);
var mcpTools = await directMcp.ListToolsAsync();
Console.Error.WriteLine($"     {Dim}{mcpTools.Count} analysis tools ready{Reset}");
Console.Error.WriteLine();

Logger.LogInfo($"MCP server started with {mcpTools.Count} tools");

// Create Copilot SDK client and session
await using var copilot = new CopilotClient();

var statusDisplay = new StatusDisplay();
int toolCallCount = 0;

// Build the system prompt
var systemPromptContent = GetSystemPrompt(binlogPath);

// Create session with MCP server pointing to BinlogMcp
var mcpServerArgs = server.Value.args.ToList();
var mcpCommand = server.Value.command;

var session = await copilot.CreateSessionAsync(new SessionConfig
{
    Model = "claude-opus-4.7",
    Streaming = true,
    SystemMessage = new SystemMessageConfig
    {
        Content = systemPromptContent,
    },
    McpServers = new Dictionary<string, McpServerConfig>
    {
        ["binlog-mcp"] = new McpStdioServerConfig
        {
            Command = mcpCommand,
            Args = mcpServerArgs,
            Tools = ["*"],
        },
    },
    OnPermissionRequest = PermissionHandler.ApproveAll,
    Hooks = new SessionHooks
    {
        OnPreToolUse = (input, _) =>
        {
            Logger.LogToolCall(input.ToolName, input.ToolArgs?.ToString() ?? "{}");
            Interlocked.Increment(ref toolCallCount);
            statusDisplay.ShowStatus();
            return Task.FromResult<PreToolUseHookOutput?>(
                new PreToolUseHookOutput { PermissionDecision = "allow" });
        },
        OnPostToolUse = (input, _) =>
        {
            var resultPreview = input.ToolResult?.ToString() ?? "";
            Logger.LogToolResult(input.ToolName, resultPreview.Length > 200 ? resultPreview[..200] : resultPreview);
            return Task.FromResult<PostToolUseHookOutput?>(null);
        },
    },
});

// Show welcome message
PrintWelcome();

// REPL loop
while (true)
{
    Console.Error.Write($"{BrightCyan}binlog>{Reset} ");
    var input = Console.ReadLine()?.Trim();

    if (string.IsNullOrEmpty(input))
        continue;

    Logger.LogUserInput(input);

    var lowerInput = input.ToLowerInvariant();

    // Handle commands
    if (lowerInput is "exit" or "quit" or "q")
    {
        Console.Error.WriteLine($"{Dim}Goodbye!{Reset}");
        Logger.LogInfo("Session ended by user");
        break;
    }

    if (lowerInput is "help" or "?" or "4")
    {
        PrintHelp();
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
        Console.Error.WriteLine($"{Green}Baseline set:{Reset} {White}{Path.GetFileName(baselinePath)}{Reset}");
        Console.Error.WriteLine($"{Dim}Use 'compare' or 'visualize comparison' to compare builds.{Reset}");
        continue;
    }

    if (lowerInput is "clear baseline")
    {
        baselinePath = null;
        Console.Error.WriteLine($"{Dim}Baseline cleared.{Reset}");
        continue;
    }

    // Visualization commands (direct MCP calls, no LLM needed)
    if (lowerInput.StartsWith("visualize ") || lowerInput.StartsWith("viz ") || lowerInput is "2" or "3")
    {
        var vizType = lowerInput switch
        {
            "2" => "timeline",
            "3" => "slowest",
            _ => lowerInput.Substring(lowerInput.StartsWith("visualize ") ? 10 : 4).Trim()
        };

        await HandleVisualize(vizType, binlogPath, baselinePath, directMcp, statusDisplay);
        continue;
    }

    // Diagnose command
    if (lowerInput is "diagnose" or "yes" or "y" or "1")
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"{BrightCyan}Running AIDavid diagnosis...{Reset}");
        Console.Error.WriteLine();

        toolCallCount = 0;
        var stats = await SendAndRender(session, statusDisplay,
            "Analyze this build and provide a comprehensive diagnosis. Follow the investigation process in your instructions.",
            outputFile: "diag.md");

        // Print stats footer
        PrintStats(stats, toolCallCount);

        Console.Error.WriteLine();
        continue;
    }

    // Compare command
    if (lowerInput is "compare" or "comparison")
    {
        if (baselinePath == null)
        {
            Console.Error.WriteLine("No baseline set. Use 'set baseline <path>' first.");
            continue;
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"{BrightCyan}Comparing:{Reset} {Dim}{Path.GetFileName(baselinePath)} → {Path.GetFileName(binlogPath)}{Reset}");
        Console.Error.WriteLine();

        await SendAndRender(session, statusDisplay,
            $"Compare the baseline build ({baselinePath}) with the current build ({binlogPath}). " +
            "Use ComparePerformance, DiffTargetExecution, and DiffProperties to show the key differences. " +
            "Focus on timing changes, what ran differently, and any important property changes.");

        Console.Error.WriteLine();
        continue;
    }

    // Regular conversation - ask a question
    Console.Error.WriteLine();

    await SendAndRender(session, statusDisplay, input);

    Console.Error.WriteLine();
}

Logger.Flush();
return 0;

// ============================================================================
// Send a prompt to the Copilot SDK session and render the response
// ============================================================================

static async Task<(long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, double elapsedSeconds)> SendAndRender(
    CopilotSession session,
    StatusDisplay statusDisplay,
    string prompt,
    string? outputFile = null)
{
    Logger.LogInfo($"[PROMPT] {prompt}");
    statusDisplay.ShowStatus();

    var responseBuilder = new StringBuilder();
    var done = new TaskCompletionSource();
    var sw = Stopwatch.StartNew();

    long totalInputTokens = 0;
    long totalOutputTokens = 0;
    long totalCacheReadTokens = 0;
    long totalCacheWriteTokens = 0;

    session.On(evt =>
    {
        switch (evt)
        {
            case AssistantMessageDeltaEvent delta:
                statusDisplay.ShowStatus();
                responseBuilder.Append(delta.Data.DeltaContent);
                break;

            case AssistantUsageEvent usage:
                totalInputTokens += (long)(usage.Data.InputTokens ?? 0);
                totalOutputTokens += (long)(usage.Data.OutputTokens ?? 0);
                totalCacheReadTokens += (long)(usage.Data.CacheReadTokens ?? 0);
                totalCacheWriteTokens += (long)(usage.Data.CacheWriteTokens ?? 0);
                break;

            case SessionIdleEvent:
                done.TrySetResult();
                break;
        }
    });

    try
    {
        await session.SendAsync(new MessageOptions { Prompt = prompt });
        await done.Task;
    }
    catch (Exception ex)
    {
        statusDisplay.ClearStatus();
        Logger.LogError($"Error: {ex.Message}", ex);
        Console.Error.WriteLine($"Error: {ex.Message}");
    }

    sw.Stop();
    statusDisplay.ClearStatus();

    var finalResponse = responseBuilder.ToString();
    if (!string.IsNullOrEmpty(finalResponse))
    {
        // Render markdown nicely to console
        MarkdownRenderer.Render(finalResponse);

        // Write raw markdown to file if requested
        if (outputFile != null)
        {
            var fullPath = Path.GetFullPath(outputFile);
            await File.WriteAllTextAsync(fullPath, finalResponse);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"{Dim}Saved to {fullPath}{Reset}");
        }

        Logger.LogCompletion(0, finalResponse);
    }

    return (
        totalInputTokens,
        totalOutputTokens,
        totalCacheReadTokens,
        totalCacheWriteTokens,
        sw.Elapsed.TotalSeconds);
}

static void PrintStats(
    (long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, double elapsedSeconds) stats,
    int toolCalls)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{Dim}───────────────────────────────────────────────────{Reset}");

    var minutes = (int)(stats.elapsedSeconds / 60);
    var seconds = stats.elapsedSeconds % 60;
    var timeStr = minutes > 0 ? $"{minutes}m {seconds:F0}s" : $"{seconds:F1}s";

    Console.Error.WriteLine($"{Dim}  ⏱  {timeStr}  │  {toolCalls} tool calls{Reset}");

    var nonCached = stats.inputTokens - stats.cacheReadTokens;
    Console.Error.WriteLine($"{Dim}  📊 Input: {stats.inputTokens:N0} tokens ({nonCached:N0} non-cached, {stats.cacheReadTokens:N0} cached){Reset}");
    Console.Error.WriteLine($"{Dim}     Output: {stats.outputTokens:N0} tokens  │  Cache writes: {stats.cacheWriteTokens:N0}{Reset}");
    Console.Error.WriteLine($"{Dim}───────────────────────────────────────────────────{Reset}");
}

// ============================================================================
// System prompt
// ============================================================================

static string GetSystemPrompt(string binlogPath) => $"""
    You are AIDavid, a virtual MSBuild debugging expert inspired by David Federman's methodology
    (dfederm.com/debugging-msbuild).

    You are analyzing this binlog file: {binlogPath}
    When calling tools that require a binlog path, always use: {binlogPath}

    ## Your Debugging Philosophy

    Every MSBuild investigation answers two fundamental questions:
    1. **"What happened?"** — Examine target execution, property values, items, and actual build results
    2. **"Why did it happen?"** — Trace the logic: imports, conditions, property origins, target dependencies

    **CRITICAL: Always pursue root cause.** Don't just explain what an error message says — dig into WHY
    it happened. Error messages can be misleading. The binlog contains the actual truth — task inputs,
    property values, and the embedded source files. Trust the data, not the error text.

    ## Key Investigation Techniques

    ### Trace Task Inputs (most important technique)
    When a task produces an error, the error message alone is NOT the root cause. You must:
    1. Use `AnalyzeTarget` to inspect the target that ran the failing task
    2. Examine the task's **input parameters** — what data was fed into the task?
    3. Trace suspicious input values back to their source using `GetPropertyOrigin` / `TraceProperty`
    4. If an input references a file (config files, response files, etc.), use `GetEmbeddedSourceFile`
       to read the actual file content

    ### Read the Source Files
    The binlog embeds the actual project files used during the build. Use them!
    - `ListEmbeddedSourceFiles` to discover what's available
    - `GetEmbeddedSourceFile` to read .csproj, .props, .targets, App.config, Directory.Build.props, etc.
    - When an error references a file path, read that file to understand what it contains
    - Cross-reference what the error says against what the file actually contains — they may disagree

    ### Verify Claims Against Data
    Error messages, especially from ResolveAssemblyReference (RAR), can be misleading:
    - RAR may report that assembly X "depends on" version Y, but this can be an artifact of
      binding redirects or other unification logic, not the actual assembly metadata
    - Always verify version claims by checking actual NuGet package contents, assembly references,
      and the properties/items that feed into the task
    - If something seems wrong, check the task inputs — the inputs tell you what RAR was actually given

    ### Multi-TFM Awareness
    For multi-targeting projects (net472 + net8.0, etc.):
    - Each target framework gets its own "inner build" with separate properties and items
    - Properties like `AppConfigFile`, `TargetFramework`, binding redirects may differ per inner build
    - BUT some properties (like AppConfigFile) may incorrectly leak across TFMs — this is a common
      source of bugs. MSBuild's RAR reads App.config for ALL frameworks, even net8.0 where binding
      redirects don't apply at runtime
    - Use `SearchBinlog` with TFM-specific queries to check per-framework values

    ## Understanding Build Structure

    - **dirs.proj, *.sln, *.slnx files** are aggregation/traversal projects. They span the entire build
      time by design — don't report these as slow. Focus on actual projects (*.csproj, *.vbproj, etc.).
    - Use GetProjectPerformance or GetProjectDependencies to find real bottlenecks underneath aggregators.

    ## Your Investigation Process

    ### Phase 1: Understand the Build Outcome
    - Start with GetBuildSummary to understand success/failure, duration, scope
    - If failed, use GetErrors to see exact error messages and locations
    - Use GetFailureDiagnosis for categorized root cause analysis

    ### Phase 2: Investigate "What Happened"
    - Use GetTargets/GetTasks to see what executed and timing
    - Use GetProperties and GetItems to examine key values
    - Use GetTargetExecutionReasons to understand target flow
    - Use GetSkippedTargets to see what DIDN'T run and why

    ### Phase 3: Deep Root Cause — "Why It Happened"
    **Don't stop at the error message. Trace the data flow.**
    - Use `AnalyzeTarget` on the target that produced the error to see task inputs/outputs
    - Use GetPropertyOrigin to trace important properties to their source files
    - Use TraceProperty for the full evaluation history of a property
    - Use GetImportChain to understand the project structure and imports
    - Use ListEmbeddedSourceFiles and GetEmbeddedSourceFile to read the actual project files
      (.csproj, .props, .targets, Directory.Build.props, App.config, etc.) embedded in the binlog
    - **When a task input references a file, READ THAT FILE.** Config files like App.config,
      NuGet.config, and .props files often contain the real root cause.
    - Use SearchBinlog to find specific values or patterns
    - For incremental build issues, use GetIncrementalBuildAnalysis

    ### Phase 4: Verify Your Hypothesis
    Before presenting a root cause, verify it:
    - Does the data support your claim? Check the actual values in the binlog.
    - Could there be a simpler explanation? Check config files and task inputs.
    - If the error message says X depends on version Y, verify that's actually true — check the
      assembly references, not just what the error reports.

    ### Phase 5: Synthesize and Recommend
    - Connect the dots between symptoms and root causes
    - Provide specific, actionable fix recommendations
    - Explain the "why" behind each recommendation
    - Never recommend simply suppressing a warning/error without addressing the underlying cause

    ## Output Format

    Structure your diagnosis as:

    ## Build Overview
    [Quick summary of what this build was trying to do and the outcome]

    ## What Happened
    [Factual description of the build execution, errors, key events]

    ## Why It Happened
    [Root cause analysis — trace the issue back to its source using the actual project files and task inputs]

    ## Recommendations
    [Specific, actionable steps to fix the issue, ordered by priority]

    ## Additional Observations (if relevant)
    [Performance issues, warnings worth addressing, other improvements]

    ---

    Be direct, specific, and actionable. Explain MSBuild concepts clearly.
    You are the debugging expert developers wish they had access to.
    """;

// ============================================================================
// Helper functions
// ============================================================================

static void PrintBanner()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {BrightCyan}{Bold}▐█▌ BinlogMCP{Reset} {Dim}───────────────────────────────────{Reset}");
    Console.Error.WriteLine($"  {Yellow}🔨{Reset} {White}Build Log Investigator{Reset}");
    Console.Error.WriteLine($"     {Dim}inspired by dfederm.com/debugging-msbuild{Reset}");
    Console.Error.WriteLine();
}

static void PrintWelcome()
{
    Console.Error.WriteLine($"  {Dim}───────────────────────────────────────────────────{Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {White}Quick start:{Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"    {BrightGreen}[1]{Reset} {Green}diagnose{Reset}            Automated build analysis");
    Console.Error.WriteLine($"    {BrightGreen}[2]{Reset} {Green}visualize timeline{Reset}  Gantt chart of execution");
    Console.Error.WriteLine($"    {BrightGreen}[3]{Reset} {Green}visualize slowest{Reset}   Slowest targets chart");
    Console.Error.WriteLine($"    {BrightGreen}[4]{Reset} {Green}help{Reset}                All commands");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {Dim}Or ask: \"Why did this build fail?\"{Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"  {Dim}───────────────────────────────────────────────────{Reset}");
    Console.Error.WriteLine();
}

static void PrintHelp()
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{White}{Bold}Commands{Reset}");
    Console.Error.WriteLine($"  {Green}diagnose{Reset}              Run automated AIDavid diagnosis");
    Console.Error.WriteLine($"  {Green}help{Reset}                  Show this help");
    Console.Error.WriteLine($"  {Green}log{Reset}                   Show log file path");
    Console.Error.WriteLine($"  {Green}exit{Reset}                  Exit the program");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{White}{Bold}Visualization{Reset}");
    Console.Error.WriteLine($"  {Cyan}visualize timeline{Reset}    Gantt chart of build execution");
    Console.Error.WriteLine($"  {Cyan}visualize slowest{Reset}     Bar chart of slowest targets");
    Console.Error.WriteLine($"  {Cyan}visualize comparison{Reset}  Compare with baseline {Dim}(requires baseline){Reset}");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{White}{Bold}Comparison{Reset}");
    Console.Error.WriteLine($"  {Yellow}set baseline <path>{Reset}   Set a baseline binlog for comparison");
    Console.Error.WriteLine($"  {Yellow}clear baseline{Reset}        Remove the baseline");
    Console.Error.WriteLine($"  {Yellow}compare{Reset}               Compare current build with baseline");
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{Dim}Or just ask a question about your build:{Reset}");
    Console.Error.WriteLine($"{Dim}  \"Why did this build fail?\"");
    Console.Error.WriteLine($"  \"What targets took the longest?\"");
    Console.Error.WriteLine($"  \"Show me the errors\"{Reset}");
    Console.Error.WriteLine();
}

static void PrintUsage()
{
    Console.Error.WriteLine("BinlogMCP - MSBuild Binary Log Analyzer");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  BinlogMcp.Client [binlog-path]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("If no binlog path is provided, you will be prompted to enter one.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --help, -h           Show this help");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Examples:");
    Console.Error.WriteLine("  BinlogMcp.Client ./build.binlog");
    Console.Error.WriteLine("  BinlogMcp.Client");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Requirements:");
    Console.Error.WriteLine("  Authenticate with GitHub CLI: gh auth login");
    Console.Error.WriteLine("  The Copilot SDK uses your GitHub token for model access.");
}

// ============================================================================
// Visualization commands (direct MCP calls, bypassing LLM)
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

    var args = new JsonObject { ["binlogPath"] = binlogPath, ["level"] = "targets" };
    var json = await mcp.CallToolAsync("GetTimeline", args);

    statusDisplay.ClearStatus();

    var data = ChartRenderer.ParseTimelineJson(json, $"Build Timeline - {Path.GetFileName(binlogPath)}");
    var result = ChartRenderer.RenderAndOpenTimeline(data);

    Console.Error.WriteLine($"{Green}Timeline saved:{Reset} {Dim}{result.FilePath}{Reset}");
    Console.Error.WriteLine($"{Dim}Opening in browser...{Reset}");
}

static async Task VisualizeSlowest(string binlogPath, McpClient mcp, StatusDisplay statusDisplay)
{
    statusDisplay.ShowStatus("Analyzing performance...");

    var args = new JsonObject { ["binlogPath"] = binlogPath, ["top"] = 30 };
    var json = await mcp.CallToolAsync("GetTargets", args);

    statusDisplay.ClearStatus();

    var data = ChartRenderer.ParsePerformanceJson(json, $"Slowest Targets - {Path.GetFileName(binlogPath)}", "targets");
    var result = ChartRenderer.RenderAndOpenBarChart(data);

    Console.Error.WriteLine($"{Green}Chart saved:{Reset} {Dim}{result.FilePath}{Reset}");
    Console.Error.WriteLine($"{Dim}Opening in browser...{Reset}");
}

static async Task VisualizeComparison(string currentPath, string baselinePath, McpClient mcp, StatusDisplay statusDisplay)
{
    statusDisplay.ShowStatus("Comparing builds...");

    var baselineArgs = new JsonObject { ["binlogPath"] = baselinePath, ["top"] = 30 };
    var currentArgs = new JsonObject { ["binlogPath"] = currentPath, ["top"] = 30 };

    var baselineJson = await mcp.CallToolAsync("GetTargets", baselineArgs);
    var currentJson = await mcp.CallToolAsync("GetTargets", currentArgs);

    statusDisplay.ClearStatus();

    var data = ChartRenderer.ParseComparisonJson(
        baselineJson,
        currentJson,
        Path.GetFileName(baselinePath),
        Path.GetFileName(currentPath),
        "Build Performance Comparison",
        "targets");

    var result = ChartRenderer.RenderAndOpenBarChart(data);

    Console.Error.WriteLine($"{Green}Comparison saved:{Reset} {Dim}{result.FilePath}{Reset}");
    Console.Error.WriteLine($"{Dim}Opening in browser...{Reset}");
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
