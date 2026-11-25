using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Beacon.Services;

/// <summary>
/// Converts a small, safe subset of Markdown and common HTML tags into an
/// Avalonia <see cref="TextBlock"/>. No browser engine and no network access
/// are involved; unknown tags are simply stripped.
/// </summary>
public static class RichTextParser
{
    private static readonly Regex InlinePattern = new(
        @"(?<bold>\*\*[^*]+\*\*)|(?<strike>~~[^~]+~~)|(?<code>`[^`]+`)|(?<link>\[[^\]]+\]\([^)]+\))|(?<italic>\*[^*]+\*)|(?<underline>_[^_]+_)",
        RegexOptions.Compiled);

    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(224, 232, 248));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.FromRgb(158, 178, 210));
    private static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(110, 190, 255));
    private static readonly IBrush CodeBrush = new SolidColorBrush(Color.FromRgb(125, 220, 255));
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.FromRgb(22, 31, 51));
    private static readonly FontFamily BodyFont = new("Segoe UI");
    private static readonly FontFamily CodeFont = new("Consolas, Monaco, monospace");

    /// <summary>
    /// Builds a TextBlock whose inlines carry all supported formatting. The
    /// caller owns the returned control and may place it anywhere in the tree.
    /// </summary>
    public static TextBlock ToTextBlock(string? input)
    {
        var block = new TextBlock
        {
            FontFamily = BodyFont,
            FontSize = 14,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        };
        var inlines = block.Inlines ??= new InlineCollection();

        var source = Normalize(input);
        if (string.IsNullOrWhiteSpace(source))
        {
            block.Text = "";
            return block;
        }

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var inCodeFence = false;
        var codeLines = new List<string>();
        var wroteAnything = false;

        void BreakLine()
        {
            inlines.Add(new LineBreak());
            wroteAnything = false;
        }

        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            var code = new Span { Foreground = CodeBrush, FontFamily = CodeFont, FontSize = 12.5 };
            code.Inlines.Add(new Run(string.Join("\n", codeLines)) { Background = CodeBackground });
            inlines.Add(code);
            codeLines.Clear();
            wroteAnything = true;
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeFence)
                {
                    FlushCode();
                    BreakLine();
                    inCodeFence = false;
                }
                else
                {
                    inCodeFence = true;
                }
                continue;
            }

            if (inCodeFence)
            {
                codeLines.Add(line);
                continue;
            }

            var content = line.TrimStart();
            if (string.IsNullOrWhiteSpace(content))
            {
                if (wroteAnything) BreakLine();
                continue;
            }

            if (wroteAnything) BreakLine();
            var indent = line.Length - content.Length;

            if (Regex.IsMatch(content, @"^([-*•]|\d+\.)\s+"))
            {
                var item = Regex.Replace(content, @"^([-*•]|\d+\.)\s+", "");
                inlines.Add(new Run("•  ") { Foreground = MutedBrush });
                AddInlines(inlines, item);
            }
            else if (content.StartsWith("### ", StringComparison.Ordinal))
            {
                AddStyledLine(inlines, content[4..], 16, FontWeight.SemiBold);
            }
            else if (content.StartsWith("## ", StringComparison.Ordinal))
            {
                AddStyledLine(inlines, content[3..], 18, FontWeight.SemiBold);
            }
            else if (content.StartsWith("# ", StringComparison.Ordinal))
            {
                AddStyledLine(inlines, content[2..], 21, FontWeight.Bold);
            }
            else if (content.StartsWith("> ", StringComparison.Ordinal))
            {
                var quote = new Span { Foreground = MutedBrush, FontStyle = FontStyle.Italic };
                quote.Inlines.Add(new Run("▌  ") { Foreground = new SolidColorBrush(Color.FromRgb(92, 155, 255)) });
                AddInlines(quote.Inlines, content[2..]);
                inlines.Add(quote);
            }
            else if (Regex.IsMatch(content, @"^-{3,}$"))
            {
                inlines.Add(new Run("────────────────") { Foreground = MutedBrush });
            }
            else
            {
                if (indent > 0) inlines.Add(new Run("    "));
                AddInlines(inlines, content);
            }

            wroteAnything = true;
        }

        FlushCode();
        return block;
    }

    /// <summary>
    /// Converts the common HTML tags used by notification senders into the
    /// Markdown subset understood by <see cref="ToTextBlock"/>, then decodes
    /// HTML entities. Everything else is discarded.
    /// </summary>
    public static string Normalize(string? input)
    {
        var text = input ?? "";
        if (!Regex.IsMatch(text, @"<\s*(br|p|div|b|strong|i|em|u|s|ul|ol|li|h[1-6]|a|code|blockquote)\b",
                RegexOptions.IgnoreCase))
            return text;

        text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(p|div|li|h[1-6]|ul|ol|blockquote)\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(p|div|ul|ol)\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*blockquote\s*>", "> ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(h[1-6])\s*>",
            match => new string('#', int.Parse(match.Groups[1].Value[1..])) + " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*li\s*>", "- ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(b|strong)\s*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(b|strong)\s*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(i|em)\s*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(i|em)\s*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(u)\s*>", "_", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(u)\s*>", "_", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(s|strike)\s*>", "~~", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(s|strike)\s*>", "~~", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*code\s*>", "`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*code\s*>", "`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*a\s+href\s*=\s*[""']([^""']+)[""'][^>]*>", "[$1]($1)", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*a\s+[^>]*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*a\s*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        return WebUtility.HtmlDecode(text);
    }

    private static void AddStyledLine(InlineCollection inlines, string text, double size, FontWeight weight)
    {
        var span = new Span { FontSize = size, FontWeight = weight, Foreground = Brushes.White };
        AddInlines(span.Inlines, text);
        inlines.Add(span);
    }

    private static void AddInlines(InlineCollection inlines, string text)
    {
        var matches = InlinePattern.Matches(text);
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index > cursor)
                inlines.Add(new Run(text[cursor..match.Index]));

            var value = match.Value;
            if (match.Groups["bold"].Success)
            {
                inlines.Add(new Run(value[2..^2]) { FontWeight = FontWeight.Bold, Foreground = Brushes.White });
            }
            else if (match.Groups["strike"].Success)
            {
                inlines.Add(new Run(value[2..^2]) { Foreground = MutedBrush });
            }
            else if (match.Groups["code"].Success)
            {
                inlines.Add(new Run(value[1..^1])
                {
                    FontFamily = CodeFont,
                    Foreground = CodeBrush,
                    Background = CodeBackground
                });
            }
            else if (match.Groups["link"].Success)
            {
                var inner = value[1..^1];
                var split = inner.IndexOf("](", StringComparison.Ordinal);
                if (split > 0)
                {
                    var label = inner[..split];
                    var uriText = inner[(split + 2)..];
                    if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri) &&
                        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                        inlines.Add(new Run(label) { Foreground = LinkBrush });
                    else
                        inlines.Add(new Run(label));
                }
                else
                {
                    inlines.Add(new Run(value));
                }
            }
            else if (match.Groups["italic"].Success)
            {
                inlines.Add(new Run(value[1..^1]) { FontStyle = FontStyle.Italic });
            }
            else if (match.Groups["underline"].Success)
            {
                inlines.Add(new Run(value[1..^1]) { Foreground = MutedBrush });
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
            inlines.Add(new Run(text[cursor..]));
    }
}
