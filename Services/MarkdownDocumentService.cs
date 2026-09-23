using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SemiconductorCsvAnalyzer.Services;

public sealed record MarkdownHeading(string Title, Paragraph Block);
public sealed record MarkdownDocument(FlowDocument Document, IReadOnlyList<MarkdownHeading> Headings);

/// <summary>A small offline Markdown reader. Content stays text; HTML/XAML and links are never executed.</summary>
public static class MarkdownDocumentService
{
    private static readonly Brush TextBrush = FrozenBrush(0x33, 0x41, 0x55);
    private static readonly Brush HeadingBrush = FrozenBrush(0x0F, 0x17, 0x2A);
    private static readonly Brush MutedBrush = FrozenBrush(0x64, 0x74, 0x8B);
    private static readonly Brush CodeBrush = FrozenBrush(0xF1, 0xF5, 0xF9);
    private static readonly Regex ListPrefix = new(@"^(?:[-*]\s+|\d+\.\s+)(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex InlineTokens = new(@"(\*\*[^*\r\n]+\*\*|`[^`\r\n]+`|\[[^\]\r\n]+\]\([^)\r\n]+\))", RegexOptions.CultureInvariant);

    public static string LoadUserGuide()
    {
        using var stream = typeof(MarkdownDocumentService).Assembly.GetManifestResourceStream("SemiconductorCsvAnalyzer.Docs.UserGuide.md")
            ?? throw new InvalidOperationException("内置 Markdown 资源不存在，请使用完整构建的程序。");
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    public static MarkdownDocument Render(string markdown)
    {
        var document = new FlowDocument
        {
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"), FontSize = 14,
            Foreground = TextBrush, PagePadding = new Thickness(28, 20, 28, 28),
            LineHeight = 25
        };
        var headings = new List<MarkdownHeading>();
        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        bool inCode = false;
        List? list = null;
        bool listIsOrdered = false;

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            document.Blocks.Add(ParagraphOf(paragraph.ToString()));
            paragraph.Clear();
        }

        void FlushCode()
        {
            document.Blocks.Add(new Paragraph(new Run(code.ToString().TrimEnd('\r', '\n')))
            {
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12,
                Background = CodeBrush, Padding = new Thickness(14), Margin = new Thickness(0, 8, 0, 14),
                LineHeight = 21
            });
            code.Clear();
        }

        foreach (var original in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = original.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(); list = null;
                if (inCode) FlushCode();
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.AppendLine(original); continue; }
            if (line.Length == 0) { FlushParagraph(); list = null; continue; }
            var headingLevel = line.TakeWhile(c => c == '#').Count();
            if (headingLevel is >= 1 and <= 6 && line.Length > headingLevel && line[headingLevel] == ' ')
            {
                FlushParagraph(); list = null;
                var title = line[(headingLevel + 1)..].Trim();
                var heading = ParagraphOf(title);
                heading.FontSize = headingLevel == 1 ? 28 : headingLevel == 2 ? 20 : 16;
                heading.FontWeight = FontWeights.SemiBold;
                heading.Foreground = HeadingBrush;
                heading.Margin = new Thickness(0, headingLevel == 1 ? 0 : 24, 0, 10);
                document.Blocks.Add(heading);
                if (headingLevel == 2) headings.Add(new MarkdownHeading(title, heading));
                continue;
            }
            var item = ListPrefix.Match(line);
            if (item.Success)
            {
                FlushParagraph();
                bool ordered = char.IsDigit(line[0]);
                if (list == null || listIsOrdered != ordered)
                {
                    listIsOrdered = ordered;
                    list = new List { MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Padding = new Thickness(22, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
                    document.Blocks.Add(list);
                }
                var listParagraph = ParagraphOf(item.Groups[1].Value);
                listParagraph.Margin = new Thickness(0, 0, 0, 6);
                list.ListItems.Add(new ListItem(listParagraph));
                continue;
            }
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph(); list = null;
                var quote = ParagraphOf(line[2..]);
                quote.Foreground = MutedBrush;
                quote.BorderBrush = FrozenBrush(0xBF, 0xDB, 0xFE);
                quote.BorderThickness = new Thickness(3, 0, 0, 0);
                quote.Padding = new Thickness(12, 4, 0, 4);
                document.Blocks.Add(quote);
                continue;
            }
            list = null;
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line);
        }
        FlushParagraph();
        if (inCode) FlushCode();
        return new MarkdownDocument(document, headings);
    }

    private static Paragraph ParagraphOf(string text)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        int position = 0;
        foreach (Match token in InlineTokens.Matches(text))
        {
            if (token.Index > position) paragraph.Inlines.Add(new Run(text[position..token.Index]));
            var value = token.Value;
            if (value.StartsWith("**", StringComparison.Ordinal)) paragraph.Inlines.Add(new Bold(new Run(value[2..^2])));
            else if (value[0] == '`') paragraph.Inlines.Add(new Run(value[1..^1]) { FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), Background = CodeBrush });
            else
            {
                int end = value.IndexOf("](", StringComparison.Ordinal);
                // Links remain inert text, including file:/javascript: URLs.
                paragraph.Inlines.Add(new Run($"{value[1..end]}（{value[(end + 2)..^1]}）"));
            }
            position = token.Index + token.Length;
        }
        if (position < text.Length) paragraph.Inlines.Add(new Run(text[position..]));
        return paragraph;
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
