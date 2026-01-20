using OpenAI.Responses;

#pragma warning disable OPENAI001 // Responses API is experimental

namespace BinlogMcp.Client;

/// <summary>
/// Manages conversation history for follow-up questions.
/// Maintains messages across multiple turns in the REPL using the Responses API format.
/// </summary>
public class ConversationManager
{
    private readonly List<ResponseItem> _items = new();
    private readonly string _systemPrompt;

    /// <summary>
    /// Creates a new conversation manager with the given system prompt.
    /// </summary>
    public ConversationManager(string systemPrompt)
    {
        _systemPrompt = systemPrompt;
    }

    /// <summary>
    /// Gets the system prompt for this conversation.
    /// </summary>
    public string SystemPrompt => _systemPrompt;

    /// <summary>
    /// Gets the number of user turns in the conversation.
    /// </summary>
    public int TurnCount { get; private set; }

    /// <summary>
    /// Adds a user message to the conversation.
    /// </summary>
    public void AddUserMessage(string message)
    {
        _items.Add(ResponseItem.CreateUserMessageItem(message));
        TurnCount++;
        Logger.LogDebug($"[CONVERSATION] Added user message (turn {TurnCount}): {message}");
    }

    /// <summary>
    /// Adds an assistant message to the conversation.
    /// </summary>
    public void AddAssistantMessage(string message)
    {
        _items.Add(ResponseItem.CreateAssistantMessageItem(message));
        Logger.LogDebug($"[CONVERSATION] Added assistant message");
    }

    /// <summary>
    /// Clears the conversation history.
    /// </summary>
    public void Clear()
    {
        _items.Clear();
        TurnCount = 0;
        Logger.LogInfo("[CONVERSATION] Cleared conversation history");
    }

    /// <summary>
    /// Gets all conversation items for use in the Responses API.
    /// </summary>
    public IEnumerable<ResponseItem> GetResponseItems()
    {
        return _items;
    }

    /// <summary>
    /// Gets the approximate token count based on message length.
    /// This is a rough estimate (4 chars per token).
    /// </summary>
    public int EstimatedTokenCount
    {
        get
        {
            // Rough estimate based on system prompt + items
            int totalChars = _systemPrompt.Length;
            // Items are harder to estimate without inspecting internal structure
            // Use a rough multiplier based on item count
            totalChars += _items.Count * 500; // rough average per item
            return totalChars / 4;
        }
    }
}
