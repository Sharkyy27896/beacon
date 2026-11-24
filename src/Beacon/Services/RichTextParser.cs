using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using Brushes = System.Windows.Media.Brushes;

namespace Beacon.Services;

/// <summary>
/// Converts a small, safe subset of Markdown (and common HTML tags) into a WPF
/// FlowDocument. No browser engine or network dependency is involved.
/// </summary>
public static class RichTextParser
{
    private static readonly Regex InlinePattern = new(
        @"(?<bold>\*\*[^*]+\*\*)|(?<strike>~~[^~]+~~)|(?<code>`[^`]+`)|(?<link>\[[^\]]+\]\([^)]+\))|(?<italic>\*[^*]+\*)|(?<underline>_[^_]+_)",
        RegexOptions.Compiled);

    public static FlowDocument ToDocument(string? input)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            Foreground = new SolidColorBrush(Color.FromRgb(224, 232, 248)),
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };

        var source = Normalize(input);
        if (string.IsNullOrWhiteSpace(source))
        {
            document.Blocks.Add(new Paragraph(new Run("")));
            return document;
        }

        var lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var listItems = new List<string>();
        var inCodeFence = false;
        var codeLines = new List<string>();

        void FlushList()
        {
            if (listItems.Count == 0) return;
            var list = new List { Margin = new Thickness(20, 4, 0, 10), Foreground = document.Foreground };
            foreach (var item in listItems)
            {
                var para = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AddInlines(para, item);
                list.ListItems.Add(new ListItem(para));
            }
            document.Blocks.Add(list);
            listItems.Clear();
        }

        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            var para = new Paragraph
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromRgb(180, 220, 255)),
                Background = new SolidColorBrush(Color.FromArgb(255, 16, 24, 40)),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 6, 0, 10)
            };
            para.Inlines.Add(new Run(string.Join("\n", codeLines)));
            document.Blocks.Add(para);
            codeLines.Clear();
        }

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.TrimStart().StartsWith("```"))
            {
                if (inCodeFence) { FlushCode(); inCodeFence = false; }
                else { FlushList(); inCodeFence = true; }
                continue;
            }
            if (inCodeFence) { codeLines.Add(line); continue; }

            var leading = line.Length - line.TrimStart().Length;
            var content = line.TrimStart();
            if (Regex.IsMatch(content, @"^([-*•]|\d+\.)\s+"))
            {
                listItems.Add(Regex.Replace(content, @"^([-*•]|\d+\.)\s+", ""));
                continue;
            }
            FlushList();

            if (string.IsNullOrWhiteSpace(content)) continue;

            if (content.StartsWith("### "))
                AddHeading(document, content[4..], 17, FontWeights.SemiBold);
            else if (content.StartsWith("## "))
                AddHeading(document, content[3..], 19, FontWeights.SemiBold);
            else if (content.StartsWith("# "))
                AddHeading(document, content[2..], 22, FontWeights.Bold);
            else if (content.StartsWith("> "))
            {
                var para = new Paragraph { Margin = new Thickness(12, 3, 0, 3), Foreground = new SolidColorBrush(Color.FromRgb(165, 190, 225)) };
                AddInlines(para, content[2..]);
                document.Blocks.Add(para);
            }
            else if (Regex.IsMatch(content, "^-{3,}$"))
            {
                document.Blocks.Add(new Paragraph { BorderBrush = new SolidColorBrush(Color.FromRgb(60, 78, 110)), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 8, 0, 8) });
            }
            else
            {
                var para = new Paragraph { Margin = new Thickness(0, leading > 0 ? 4 : 2, 0, 4) };
                AddInlines(para, content);
                document.Blocks.Add(para);
            }
        }
        FlushList();
        FlushCode();

        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph(new Run("")));
        return document;
    }

    private static void AddHeading(FlowDocument document, string text, double size, FontWeight weight)
    {
        var para = new Paragraph { FontSize = size, FontWeight = weight, Margin = new Thickness(0, 8, 0, 5) };
        para.Inlines.Add(new Run(text));
        document.Blocks.Add(para);
    }

    private static void AddInlines(Paragraph paragraph, string text)
    {
        var matches = InlinePattern.Matches(text);
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index > cursor)
                paragraph.Inlines.Add(new Run(text[cursor..match.Index]));

            var value = match.Value;
            if (match.Groups["bold"].Success)
                paragraph.Inlines.Add(new Run(value[2..^2]) { FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            else if (match.Groups["strike"].Success)
                paragraph.Inlines.Add(new Run(value[2..^2]) { TextDecorations = TextDecorations.Strikethrough });
            else if (match.Groups["code"].Success)
                paragraph.Inlines.Add(new Run(value[1..^1])
                {
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(125, 220, 255)),
                    Background = new SolidColorBrush(Color.FromRgb(22, 31, 51))
                });
            else if (match.Groups["link"].Success)
            {
                var inner = value[1..^1];
                var split = inner.IndexOf("](");
                if (split > 0)
                {
                    var label = inner[..split];
                    var uriText = inner[(split + 2)..];
                    if (Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
                    {
                        var link = new Hyperlink(new Run(label))
                        {
                            NavigateUri = uri,
                            Foreground = new SolidColorBrush(Color.FromRgb(110, 190, 255))
                        };
                        link.RequestNavigate += (_, e) =>
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(e.Uri!.ToString()) { UseShellExecute = true });
                            }
                            catch { /* ignore bad links */ }
                            e.Handled = true;
                        };
                        paragraph.Inlines.Add(link);
                    }
                    else paragraph.Inlines.Add(new Run(label));
                }
                else paragraph.Inlines.Add(new Run(value));
            }
            else if (match.Groups["italic"].Success)
                paragraph.Inlines.Add(new Run(value[1..^1]) { FontStyle = FontStyles.Italic });
            else if (match.Groups["underline"].Success)
                paragraph.Inlines.Add(new Run(value[1..^1]) { TextDecorations = TextDecorations.Underline });

            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length) paragraph.Inlines.Add(new Run(text[cursor..]));
    }

    private static string Normalize(string? input)
    {
        var text = input ?? "";
        if (!Regex.IsMatch(text, @"<\s*(br|p|div|b|strong|i|em|ul|li|h[1-6]|a|code)\b", RegexOptions.IgnoreCase))
            return text;

        text = Regex.Replace(text, @"<\s*br\s*/?\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(p|div|li|h[1-6]|ul|ol|blockquote)\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(h[1-6])\s*>", m => new string('#', int.Parse(m.Groups[1].Value[1..].ToString())) + " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*li\s*>", "- ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(b|strong)\s*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(b|strong)\s*>", "**", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*(i|em)\s*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*(i|em)\s*>", "*", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*code\s*>", "`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*code\s*>", "`", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*a\s+href\s*=\s*[""']([^""']+)[""']\s*>", "[$1]($1)", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*a\s+[^>]*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<\s*/\s*a\s*>", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", "");
        return WebUtility.HtmlDecode(text);
    }
}
