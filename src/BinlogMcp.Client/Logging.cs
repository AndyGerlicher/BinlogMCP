using NLog;
using NLog.Config;
using NLog.Targets;

namespace BinlogMcp.Client;

/// <summary>
/// Static logger class for logging all LLM and tool interactions to file.
/// Replaces --verbose flag with file-based logging.
/// </summary>
public static class Logger
{
    private static readonly NLog.Logger Log;
    private static readonly string LogFilePath;
    private static readonly bool IsInitialized;
    private static readonly string? InitError;

    static Logger()
    {
        try
        {
            // Create log file in current directory
            LogFilePath = Path.Combine(Directory.GetCurrentDirectory(), "binlog-client.log");

            // Configure NLog programmatically
            var config = new LoggingConfiguration();

            // File target with detailed layout
            var fileTarget = new FileTarget("file")
            {
                FileName = LogFilePath,
                Layout = "${longdate} | ${level:uppercase=true:padding=-5} | ${message}${onexception:${newline}${exception:format=tostring}}",
                KeepFileOpen = false,  // More reliable on Windows
                ConcurrentWrites = true,
                AutoFlush = true
            };

            config.AddTarget(fileTarget);
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, fileTarget);

            LogManager.Configuration = config;
            Log = LogManager.GetCurrentClassLogger();
            IsInitialized = true;

            // Write an initial entry to ensure file is created
            Log.Info("Logger initialized");
            LogManager.Flush();
        }
        catch (Exception ex)
        {
            IsInitialized = false;
            InitError = ex.Message;
            LogFilePath = "(failed to initialize)";
            Log = LogManager.CreateNullLogger();
        }
    }

    /// <summary>
    /// Gets whether logging was successfully initialized.
    /// </summary>
    public static bool Initialized => IsInitialized;

    /// <summary>
    /// Gets any initialization error message.
    /// </summary>
    public static string? Error => InitError;

    /// <summary>
    /// Gets the path to the current log file.
    /// </summary>
    public static string GetLogFilePath() => LogFilePath;

    /// <summary>
    /// Log the start of a new session.
    /// </summary>
    public static void LogSessionStart(string binlogPath, string mode)
    {
        Log.Info("═══════════════════════════════════════════════════════════════════════════════");
        Log.Info($"Session started");
        Log.Info($"Binlog: {binlogPath}");
        Log.Info($"Mode: {mode}");
        Log.Info("═══════════════════════════════════════════════════════════════════════════════");
    }

    /// <summary>
    /// Log user input from the REPL.
    /// </summary>
    public static void LogUserInput(string input)
    {
        Log.Info($"[USER INPUT] {input}");
    }

    /// <summary>
    /// Log an LLM request (system prompt, user message, etc.).
    /// </summary>
    public static void LogLlmRequest(string model, string type, string content)
    {
        Log.Debug($"[LLM REQUEST] Model: {model}");
        Log.Debug($"[LLM REQUEST] Type: {type}");
        Log.Debug($"[LLM REQUEST] Content: {Truncate(content, 2000)}");
    }

    /// <summary>
    /// Log an LLM response.
    /// </summary>
    public static void LogLlmResponse(string model, string status, string? content = null)
    {
        Log.Debug($"[LLM RESPONSE] Model: {model}");
        Log.Debug($"[LLM RESPONSE] Status: {status}");
        if (!string.IsNullOrEmpty(content))
        {
            Log.Debug($"[LLM RESPONSE] Content: {Truncate(content, 2000)}");
        }
    }

    /// <summary>
    /// Log a tool call being made.
    /// </summary>
    public static void LogToolCall(string toolName, string arguments)
    {
        Log.Info($"[TOOL CALL] {toolName}");
        Log.Debug($"[TOOL CALL] Args: {Truncate(arguments, 500)}");
    }

    /// <summary>
    /// Log the result of a tool call.
    /// </summary>
    public static void LogToolResult(string toolName, string result, bool isError = false)
    {
        var tag = isError ? "[TOOL ERROR]" : "[TOOL RESULT]";
        Log.Debug($"{tag} {toolName}: {Truncate(result, 1000)}");
    }

    /// <summary>
    /// Log iteration progress in the agentic loop.
    /// </summary>
    public static void LogIteration(int iteration, int maxIterations, int toolCalls)
    {
        Log.Debug($"[ITERATION] {iteration}/{maxIterations} (total tool calls: {toolCalls})");
    }

    /// <summary>
    /// Log completion of an analysis.
    /// </summary>
    public static void LogCompletion(int totalToolCalls, string? finalResponse = null)
    {
        Log.Info($"[COMPLETED] Total tool calls: {totalToolCalls}");
        if (!string.IsNullOrEmpty(finalResponse))
        {
            Log.Debug($"[FINAL RESPONSE] {Truncate(finalResponse, 2000)}");
        }
    }

    /// <summary>
    /// Log an error.
    /// </summary>
    public static void LogError(string message, Exception? ex = null)
    {
        if (ex != null)
        {
            Log.Error(ex, message);
        }
        else
        {
            Log.Error(message);
        }
    }

    /// <summary>
    /// Log a warning.
    /// </summary>
    public static void LogWarning(string message)
    {
        Log.Warn(message);
    }

    /// <summary>
    /// Log general info.
    /// </summary>
    public static void LogInfo(string message)
    {
        Log.Info(message);
    }

    /// <summary>
    /// Log debug information.
    /// </summary>
    public static void LogDebug(string message)
    {
        Log.Debug(message);
    }

    /// <summary>
    /// Flush all pending log entries.
    /// </summary>
    public static void Flush()
    {
        LogManager.Flush();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + "... [truncated]";
    }
}
