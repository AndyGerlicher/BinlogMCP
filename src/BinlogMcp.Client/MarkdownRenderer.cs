using System.Text.RegularExpressions;
using Spectre.Console;

namespace BinlogMcp.Client;

/// <summary>
/// Renders markdown text to the console using Spectre.Console.
/// </summary>
public static partial class MarkdownRenderer
{
    // Pre-compiled regex patterns for inline markdown formatting
    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldStarsRegex();

    [GeneratedRegex(@"__(.+?)__")]
    private static partial Regex BoldUnderscoreRegex();

    [GeneratedRegex(@"\*(.+?)\*")]
    private static partial Regex ItalicStarRegex();

    [GeneratedRegex(@"_(.+?)_")]
    private static partial Regex ItalicUnderscoreRegex();

    [GeneratedRegex(@"`(.+?)`")]
    private static partial Regex InlineCodeRegex();

    /// <summary>
    /// Renders markdown text to the console with nice formatting.
    /// </summary>
    public static void Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return;

        // Process line by line for better control
        var lines = markdown.Split('\n');
        var inCodeBlock = false;
        var codeBlockLines = new List<string>();
        var codeBlockLang = "";

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Handle code blocks
            if (line.StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    // End of code block - render it
                    RenderCodeBlock(codeBlockLines, codeBlockLang);
                    codeBlockLines.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    // Start of code block
                    inCodeBlock = true;
                    codeBlockLang = line.Length > 3 ? line[3..].Trim() : "";
                }
                continue;
            }

            if (inCodeBlock)
            {
                codeBlockLines.Add(line);
                continue;
            }

            // Handle different markdown elements
            if (string.IsNullOrWhiteSpace(line))
            {
                AnsiConsole.WriteLine();
            }
            else if (line.StartsWith("# "))
            {
                RenderHeading1(line[2..]);
            }
            else if (line.StartsWith("## "))
            {
                RenderHeading2(line[3..]);
            }
            else if (line.StartsWith("### "))
            {
                RenderHeading3(line[4..]);
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                RenderBullet(line[2..], 0);
            }
            else if (line.StartsWith("  - ") || line.StartsWith("  * "))
            {
                RenderBullet(line[4..], 1);
            }
            else if (line.StartsWith("    - ") || line.StartsWith("    * "))
            {
                RenderBullet(line[6..], 2);
            }
            else if (line.StartsWith("> "))
            {
                RenderBlockquote(line[2..]);
            }
            else if (line.StartsWith("---") || line.StartsWith("***"))
            {
                RenderHorizontalRule();
            }
            else if (char.IsDigit(line[0]) && line.Contains(". "))
            {
                var idx = line.IndexOf(". ");
                RenderNumberedItem(line[(idx + 2)..], line[..idx]);
            }
            else
            {
                RenderParagraph(line);
            }
        }

        // Handle unclosed code block
        if (inCodeBlock && codeBlockLines.Count > 0)
        {
            RenderCodeBlock(codeBlockLines, codeBlockLang);
        }
    }

    private static void RenderHeading1(string text)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold cyan]{EscapeMarkup(text)}[/]");
        AnsiConsole.MarkupLine("[dim]" + new string('─', Math.Min(text.Length + 4, 50)) + "[/]");
    }

    private static void RenderHeading2(string text)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold white]{EscapeMarkup(text)}[/]");
    }

    private static void RenderHeading3(string text)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]{EscapeMarkup(text)}[/]");
    }

    private static void RenderBullet(string text, int indent)
    {
        var prefix = new string(' ', indent * 2);
        var bullet = indent == 0 ? "•" : indent == 1 ? "◦" : "▪";
        var formatted = FormatInlineMarkup(text);
        AnsiConsole.MarkupLine($"{prefix}[green]{bullet}[/] {formatted}");
    }

    private static void RenderNumberedItem(string text, string number)
    {
        var formatted = FormatInlineMarkup(text);
        AnsiConsole.MarkupLine($"[yellow]{number}.[/] {formatted}");
    }

    private static void RenderBlockquote(string text)
    {
        var formatted = FormatInlineMarkup(text);
        AnsiConsole.MarkupLine($"[dim]│[/] [italic]{formatted}[/]");
    }

    private static void RenderHorizontalRule()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]" + new string('─', 50) + "[/]");
        AnsiConsole.WriteLine();
    }

    private static void RenderParagraph(string text)
    {
        var formatted = FormatInlineMarkup(text);
        AnsiConsole.MarkupLine(formatted);
    }

    private static void RenderCodeBlock(List<string> lines, string language)
    {
        AnsiConsole.WriteLine();

        var panel = new Panel(string.Join("\n", lines.Select(EscapeMarkup)))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Grey),
            Padding = new Padding(1, 0, 1, 0)
        };

        if (!string.IsNullOrEmpty(language))
        {
            panel.Header = new PanelHeader($"[dim]{language}[/]", Justify.Left);
        }

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Formats inline markdown (bold, italic, code, links).
    /// </summary>
    private static string FormatInlineMarkup(string text)
    {
        var result = EscapeMarkup(text);

        // Bold: **text** or __text__
        result = BoldStarsRegex().Replace(result, "[bold]$1[/]");
        result = BoldUnderscoreRegex().Replace(result, "[bold]$1[/]");

        // Italic: *text* or _text_
        result = ItalicStarRegex().Replace(result, "[italic]$1[/]");
        result = ItalicUnderscoreRegex().Replace(result, "[italic]$1[/]");

        // Inline code: `code`
        result = InlineCodeRegex().Replace(result, "[cyan]$1[/]");

        return result;
    }

    /// <summary>
    /// Escapes special Spectre.Console markup characters.
    /// </summary>
    private static string EscapeMarkup(string text)
    {
        return text.Replace("[", "[[").Replace("]", "]]");
    }
}
