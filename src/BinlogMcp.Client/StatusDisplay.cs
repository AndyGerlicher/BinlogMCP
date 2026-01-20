namespace BinlogMcp.Client;

/// <summary>
/// Displays rotating status messages during analysis.
/// Uses Console.Error so it doesn't interfere with the final output.
/// </summary>
public class StatusDisplay
{
    private static readonly string[] StatusWords =
    [
        "Investigating...",
        "Analyzing...",
        "Tracing...",
        "Examining...",
        "Reviewing...",
        "Checking...",
        "Exploring...",
        "Inspecting...",
        "Evaluating...",
        "Processing..."
    ];

    private int _currentIndex;
    private int _lastLineLength;
    private bool _isActive;

    /// <summary>
    /// Shows the next rotating status word on the current line.
    /// Clears the previous status before showing the new one.
    /// </summary>
    public void ShowStatus()
    {
        ClearCurrentLine();

        var status = StatusWords[_currentIndex];
        _currentIndex = (_currentIndex + 1) % StatusWords.Length;

        Console.Error.Write($"  {status}");
        _lastLineLength = status.Length + 2;
        _isActive = true;
    }

    /// <summary>
    /// Shows a custom status message.
    /// </summary>
    public void ShowStatus(string message)
    {
        ClearCurrentLine();

        Console.Error.Write($"  {message}");
        _lastLineLength = message.Length + 2;
        _isActive = true;
    }

    /// <summary>
    /// Clears the current status line.
    /// </summary>
    public void ClearStatus()
    {
        if (_isActive)
        {
            ClearCurrentLine();
            _isActive = false;
        }
    }

    /// <summary>
    /// Clears the status and moves to a new line.
    /// </summary>
    public void EndStatus()
    {
        if (_isActive)
        {
            ClearCurrentLine();
            Console.Error.WriteLine();
            _isActive = false;
        }
    }

    private void ClearCurrentLine()
    {
        if (_lastLineLength > 0)
        {
            // Move to start of line and clear with spaces
            Console.Error.Write('\r');
            Console.Error.Write(new string(' ', _lastLineLength));
            Console.Error.Write('\r');
            _lastLineLength = 0;
        }
    }

    /// <summary>
    /// Resets the status word rotation to the beginning.
    /// </summary>
    public void Reset()
    {
        _currentIndex = 0;
        _lastLineLength = 0;
        _isActive = false;
    }
}
